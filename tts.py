"""
TTS 합성 + 재생 스레드.

메인 폴링 루프를 절대 블로킹하지 않도록 합성/재생은 전부 VoiceWorker
스레드에서 처리한다. CRITICAL 이벤트가 대기 중이면 재생 중인 저우선순위
멘트를 중단하고 끼어든다.

엔진은 교체 가능: kokoro (배포 음성 팩) / edge / elevenlabs. 의존성이 없거나
합성이 실패해도 앱은 죽지 않고 콘솔 텍스트로만 동작한다. (GPU 로컬 TTS는 게임과
GPU를 경쟁하므로 지원하지 않는다.)

한 목소리 정책: 엔진을 띄울 때 세션 목소리를 하나 정하고(build_engine), 모든
콜은 그 목소리로 나가거나 아예 나가지 않는다 (로그·최근 무전에는 남는다).
다른 목소리로 대신 말하는 건 tts.allow_fallback(기본 끔)일 때뿐.
C# VoicePolicy/VoiceRoute/RadioVoice와 같은 규칙.
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import random
import tempfile
import threading
import time
from collections import deque
import urllib.request
from typing import Callable, Optional

from events import Event, EventBus, EventType, Priority

log = logging.getLogger("tts")


def _cache_path(cache_dir: str, key: str, ext: str = "mp3") -> str:
    h = hashlib.md5(key.encode("utf-8")).hexdigest()[:20]
    return os.path.join(cache_dir, f"{h}.{ext}")


class TTSEngine:
    """text → 오디오 파일 경로. 실패 시 None (텍스트 로그로 폴백)."""

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        raise NotImplementedError


class NullTTSEngine(TTSEngine):
    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        return None


class EdgeTTSEngine(TTSEngine):
    # 톤별 전달 (기본 속도에 더할 %, 피치) — 모든 멘트가 같은 속도·피치로
    # 나가면 낭독(교과서 음원)처럼 들린다. 긴급 콜은 빠르고 살짝 올라가게,
    # 평상 콜은 피치를 내려 착 가라앉게.
    TONE_DELIVERY = {
        "casual": (0, "-6Hz"),
        "urgent": (14, "+4Hz"),
    }

    def __init__(self, cache_dir: str, voice: str, rate: str):
        self.cache_dir = cache_dir
        self.voice = voice
        self.base_rate = self._parse_rate(rate)
        os.makedirs(cache_dir, exist_ok=True)

    @staticmethod
    def _parse_rate(rate: str) -> int:
        try:
            return int(str(rate).replace("%", "").replace("+", "").strip() or 0)
        except ValueError:
            return 10

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        extra, pitch = self.TONE_DELIVERY.get(tone, self.TONE_DELIVERY["casual"])
        rate = f"{self.base_rate + extra:+d}%"
        path = _cache_path(self.cache_dir,
                           f"edge|{self.voice}|{rate}|{pitch}|{text}")
        if os.path.exists(path):
            return path
        try:
            import edge_tts
        except ImportError:
            log.warning("edge-tts 미설치 — 텍스트 출력만 합니다 (pip install edge-tts)")
            return None
        try:
            try:
                comm = edge_tts.Communicate(text, self.voice, rate=rate, pitch=pitch)
            except TypeError:
                # 구버전 edge-tts는 pitch 미지원 — 속도 차등만 적용
                comm = edge_tts.Communicate(text, self.voice, rate=rate)
            asyncio.run(comm.save(path))
            return path
        except Exception as e:   # 네트워크 등 어떤 실패에도 앱은 계속
            log.warning("Edge TTS 합성 실패: %s", e)
            try:
                if os.path.exists(path):
                    os.remove(path)
            except OSError:
                pass
            return None


class ElevenLabsEngine(TTSEngine):
    API_URL = "https://api.elevenlabs.io/v1/text-to-speech/{voice_id}"

    def __init__(self, cache_dir: str, api_key: str, voice_id: str):
        self.cache_dir = cache_dir
        self.api_key = api_key
        self.voice_id = voice_id
        os.makedirs(cache_dir, exist_ok=True)

    # 톤별 voice_settings — stability를 낮추면 전달이 더 감정적/즉흥적이 된다
    TONE_SETTINGS = {
        "casual": {"stability": 0.55, "similarity_boost": 0.75},
        "urgent": {"stability": 0.35, "similarity_boost": 0.8},
    }

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        settings = self.TONE_SETTINGS.get(tone, self.TONE_SETTINGS["casual"])
        path = _cache_path(self.cache_dir,
                           f"11labs|{self.voice_id}|{tone}|{text}")
        if os.path.exists(path):
            return path
        # 키 없이도 캐시 전용으로 동작 — 제작자가 미리 생성한 오디오를
        # 배포판에 담으면, 유저는 가입/키 없이 그 캐시만 재생한다.
        # 캐시에 없는 멘트는 None → 말하지 않는다 (allow_fallback이면 edge).
        if not self.api_key:
            return None
        body = json.dumps({
            "text": text,
            "model_id": "eleven_multilingual_v2",
            "voice_settings": settings,
        }).encode("utf-8")
        req = urllib.request.Request(
            self.API_URL.format(voice_id=self.voice_id),
            data=body,
            headers={
                "xi-api-key": self.api_key,
                "Content-Type": "application/json",
                "Accept": "audio/mpeg",
            },
        )
        try:
            with urllib.request.urlopen(req, timeout=15) as resp:
                audio = resp.read()
            with open(path, "wb") as f:
                f.write(audio)
            return path
        except Exception as e:
            log.warning("ElevenLabs 합성 실패: %s", e)
            return None


class KokoroEngine(TTSEngine):
    """
    Kokoro — 로컬 신경망 TTS (모델 라이선스 Apache 2.0, 상업 배포 자유).

    배포용 사전 생성 엔진: 제작자 PC에서 pip install kokoro-onnx 후
    tools/pregen_audio.py로 전체 멘트를 생성해 audio_cache를 배포판에
    동봉한다. 유저 PC에는 kokoro-onnx가 없으므로 캐시 전용으로 동작하고,
    캐시에 없는 멘트는 None → 말하지 않는다 (allow_fallback이면 edge).

    모델 파일(약 340MB)은 첫 생성 때 models/ 아래로 자동 다운로드 (사전 생성 도구만 —
    세션 엔진은 allow_download=False라 내려받지 않는다).
    """

    MODEL_URL = ("https://github.com/thewh1teagle/kokoro-onnx/releases/"
                 "download/model-files-v1.0/kokoro-v1.0.onnx")
    VOICES_URL = ("https://github.com/thewh1teagle/kokoro-onnx/releases/"
                  "download/model-files-v1.0/voices-v1.0.bin")

    # 톤별 말 속도 (edge-tts의 전달 차등과 같은 취지)
    TONE_SPEED = {"casual": 1.0, "urgent": 1.15}

    MODEL_FILES = ("kokoro-v1.0.onnx", "voices-v1.0.bin")

    def __init__(self, cache_dir: str, voice: str = "bm_george",
                 model_dir: str = "models", allow_download: bool = True):
        self.cache_dir = cache_dir
        self.voice = voice
        self.model_dir = model_dir
        # 세션 중(build_engine)에는 내려받지 않는다 — 340MB를 레이스 도중에 받지 않게
        self.allow_download = allow_download
        self._engine = None
        self._unavailable = False
        os.makedirs(cache_dir, exist_ok=True)

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        path = _cache_path(self.cache_dir,
                           f"kokoro|{self.voice}|{tone}|{text}", ext="wav")
        if os.path.exists(path):
            return path
        rendered = self.render(text, tone)
        if rendered is None:
            return None
        try:
            import soundfile as sf
            samples, sr = rendered
            sf.write(path, samples, sr)
            return path
        except Exception as e:
            log.warning("Kokoro 저장 실패: %s", e)
            try:
                if os.path.exists(path):
                    os.remove(path)
            except OSError:
                pass
            return None

    def render(self, text: str, tone: str = "casual",
               speed_mult: float = 1.0):
        """합성만 (파일 I/O 없음) → (samples, sr). 모델이 없거나 실패하면 None.
        캐시 키는 원문, 합성 입력만 speech_text로 정규화한다."""
        engine = self._load()
        if engine is None:
            return None
        try:
            from messages import speech_text
            lang = "en-gb" if self.voice.startswith("b") else "en-us"
            speed = self.TONE_SPEED.get(tone, 1.0) * speed_mult
            samples, sr = engine.create(speech_text(text), voice=self.voice,
                                        speed=speed, lang=lang)
            return samples, sr
        except Exception as e:
            log.warning("Kokoro 합성 실패: %s", e)
            return None

    @staticmethod
    def _module_available() -> bool:
        """kokoro-onnx 설치 여부 — import하지 않고 확인만 (무거운 onnxruntime 로드 회피)."""
        try:
            import importlib.util
            return importlib.util.find_spec("kokoro_onnx") is not None
        except (ImportError, ValueError):
            return False

    def runtime_ready(self) -> bool:
        """런타임 합성 가능 — 모듈과 모델 파일 둘 다 있고, 초기화에 실패한 적 없음."""
        if self._unavailable or not self._module_available():
            return False
        return all(os.path.isfile(os.path.join(self.model_dir, n))
                   for n in self.MODEL_FILES)

    def _load(self):
        if self._engine is not None:
            return self._engine
        if self._unavailable:
            return None
        try:
            from kokoro_onnx import Kokoro
        except ImportError:
            self._unavailable = True
            log.info("kokoro-onnx 미설치 — 캐시 전용 모드 "
                     "(생성하려면 pip install kokoro-onnx soundfile)")
            return None
        try:
            model = self._ensure_file("kokoro-v1.0.onnx", self.MODEL_URL)
            voices = self._ensure_file("voices-v1.0.bin", self.VOICES_URL)
            self._engine = Kokoro(model, voices)
            return self._engine
        except Exception as e:
            self._unavailable = True
            log.warning("Kokoro 초기화 실패 — 캐시 전용 모드: %s", e)
            return None

    def _ensure_file(self, name: str, url: str) -> str:
        path = os.path.join(self.model_dir, name)
        if os.path.exists(path):
            return path
        if not self.allow_download:
            raise FileNotFoundError(f"모델 파일 없음: {path} (세션 중에는 내려받지 않음)")
        os.makedirs(self.model_dir, exist_ok=True)
        log.info("Kokoro 모델 다운로드 중: %s (한 번만)", name)
        tmp = path + ".part"
        urllib.request.urlretrieve(url, tmp)
        os.replace(tmp, path)
        return path


class KokoroPackEngine(TTSEngine):
    """
    배포 kokoro 팩 전용 (캐시만, 합성 없음): 통문장 _rfx 클립 → 문장 세그먼트
    조립 → None 순서. C# VoiceRoute(ResolveWholeFx → 세그먼트 조립)와 같은 순서·규칙
    (kokoro_plan을 커버리지 대조와 함께 쓴다).

    조립 결과는 cache_dir/_assembled/에 파일로 남겨 재사용한다 (pygame은 파일
    재생). 파일명에 원천(세그먼트·자산)의 이름·크기·mtime 해시를 넣어, 팩을
    갈아끼우면 mtime이 더 옛날이어도(압축 해제는 mtime 보존) 새로 조립한다.
    캐시 폴더에 못 쓰면 임시 폴더로. 두 폴더 다 최신 MAX_ASSEMBLED개만 남긴다.
    """

    MAX_ASSEMBLED = 200
    MAX_MISS_LOG = 20

    def __init__(self, cache_dir: str, voice: str = "bm_george"):
        import radio_assemble
        self.cache_dir = cache_dir
        self.voice = voice
        self._ra = radio_assemble
        self._asm_dir = os.path.join(cache_dir, "_assembled")
        self._tmp_dir = os.path.join(tempfile.gettempdir(), "teamradio56")
        self._missed: set = set()
        self._prune(self._asm_dir)
        self._prune(self._tmp_dir)

    def _prune(self, d: str) -> None:
        # "_asm"이 든 이름만 — 임시 폴더의 C# assembled.wav 등은 건드리지 않는다
        try:
            names = [os.path.join(d, n) for n in os.listdir(d) if "_asm" in n]
        except OSError:
            return
        files = []
        for p in names:
            try:
                files.append((os.path.getmtime(p), p))
            except OSError:
                pass
        if len(files) <= self.MAX_ASSEMBLED:
            return
        files.sort(reverse=True)
        for _, p in files[self.MAX_ASSEMBLED:]:
            try:
                os.remove(p)
            except OSError:
                pass

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        raw = _cache_path(self.cache_dir,
                          f"kokoro|{self.voice}|{tone}|{text}", ext="wav")
        kind, found = kokoro_plan(self.cache_dir, self.voice, tone, text)
        if kind == "fx":
            return found
        if kind is None:
            if found is not None:
                self._log_miss(tone, found)
            return None
        paths = found
        tag = self._source_tag(paths + [os.path.join(self.cache_dir, n)
                                        for n in self._ra.ASSET_NAMES])
        if tag is None:
            self._log_miss(tone, None, text)
            return None
        name = (os.path.splitext(os.path.basename(raw))[0]
                + f"_asm{self._ra.SEG_VERSION}_{tag}.wav")
        for d in (self._asm_dir, self._tmp_dir):
            dst = os.path.join(d, name)
            if os.path.isfile(dst):
                return dst
        data = self._ra.build(self.cache_dir, tone, paths)
        if data is None:
            self._log_miss(tone, None, text)
            return None
        for d in (self._asm_dir, self._tmp_dir):
            dst = os.path.join(d, name)
            try:
                os.makedirs(d, exist_ok=True)
                tmp = f"{dst}.{os.getpid()}.part"
                with open(tmp, "wb") as f:
                    f.write(data)
                os.replace(tmp, dst)
            except OSError:
                continue
            self._prune(d)
            return dst
        return None

    @staticmethod
    def _source_tag(sources: list) -> Optional[str]:
        """원천 파일들의 (이름, 크기, mtime) 해시 8자 — 하나라도 못 읽으면 None."""
        h = hashlib.md5()
        try:
            for p in sources:
                st = os.stat(p)
                h.update(f"{os.path.basename(p)}|{st.st_size}|{st.st_mtime_ns}\n"
                         .encode("utf-8"))
        except OSError:
            return None
        return h.hexdigest()[:8]

    def _log_miss(self, tone: str, sentence: Optional[str],
                  text: Optional[str] = None) -> None:
        # 처음 20건만 — 어떤 문장을 팩에 더 넣어야 하는지 찾는 용도
        key = (tone, sentence, text)
        if key in self._missed or len(self._missed) >= self.MAX_MISS_LOG:
            return
        self._missed.add(key)
        if sentence is not None:
            log.info("코코로 문장 미스 [%s] %s", tone, sentence)
        else:
            log.info("코코로 세그먼트 조립 실패 [%s] %s", tone, text)


class ChainEngine(TTSEngine):
    """
    앞 엔진이 실패하면 다음 엔진으로. 같은 목소리의 소스(팩 → 원본 → 런타임)를
    잇는 데 쓴다 — 다른 목소리(edge)를 뒤에 거는 건 allow_fallback일 때만.
    """

    def __init__(self, engines: list):
        self.engines = engines

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        for engine in self.engines:
            path = engine.synth(text, tone)
            if path is not None:
                return path
        return None


class RadioFXEngine(TTSEngine):
    """내부 엔진 출력에 무전기 효과를 입히는 래퍼. 처리 결과는 파일로 캐시."""

    def __init__(self, inner: TTSEngine, noise: float = 0.004):
        self.inner = inner
        self.noise = noise

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        src = self.inner.synth(text, tone)
        if src is None:
            return None
        import radiofx
        # 효과 버전을 파일명에 넣어 알고리즘이 바뀌면 자동 재생성
        dst = os.path.splitext(src)[0] + f"_rfx{radiofx.VERSION}.wav"
        if os.path.exists(dst):
            return dst
        return radiofx.process(src, dst, noise=self.noise) or src


PACK_MARKER = "teamradio56_audio_pack.txt"     # C# AudioPack.MarkerFileName
INCOMPLETE_PREFIX = "incomplete:"               # C# AudioPack.IncompletePrefix


def read_marker(text: str) -> Optional[str]:
    """음성 팩 표식의 팩 ID — C# AudioPack.ReadMarker와 같은 규칙.
    BOM 제거, '#' 주석·빈 줄 건너뛰고 첫 줄, 'pack=' 접두는 떼어낸다."""
    for raw in text.lstrip("\ufeff").split("\n"):
        line = raw.strip()
        if not line or line[0] == "#":
            continue
        if line.startswith("pack="):
            line = line[5:].strip()
        return line or None
    return None


def probe_text() -> Optional[str]:
    """팩 확인용 문장 — en 멘트 풀 pit_call/casual 첫 줄 (모든 팩에 통문장으로 있다)."""
    from voice import PhrasePool, lines_file
    lines = PhrasePool(lines_file("en")).lines("pit_call", "casual")
    return lines[0] if lines else None


def kokoro_pack_state(cache_dir: str, voice: str = "bm_george",
                      probe: Optional[str] = None) -> str:
    """kokoro 음성 팩 상태 "ok" | "incomplete" | "none" — C# VoicePolicy.KokoroPackState.
    표식이 있으면 표식대로, 없으면 조립 자산 4개(옛 v015)나 확인용 통문장
    (_rfx3 또는 원본, 개발 캐시)이 있으면 ok."""
    import radio_assemble
    import radiofx
    if not cache_dir or not os.path.isdir(cache_dir):
        return "none"
    marker = os.path.join(cache_dir, PACK_MARKER)
    if os.path.isfile(marker):
        try:
            with open(marker, "r", encoding="utf-8", errors="replace") as f:
                pack_id = read_marker(f.read())
        except OSError:
            pack_id = None
        if pack_id is None or pack_id.startswith(INCOMPLETE_PREFIX):
            return "incomplete"
        return "ok"
    if all(os.path.isfile(os.path.join(cache_dir, n))
           for n in radio_assemble.ASSET_NAMES):
        return "ok"
    if probe is None:
        probe = probe_text()
    if probe:
        raw = _cache_path(cache_dir, f"kokoro|{voice}|casual|{probe}", ext="wav")
        rfx = os.path.splitext(raw)[0] + f"_rfx{radiofx.VERSION}.wav"
        if os.path.isfile(rfx) or os.path.isfile(raw):
            return "ok"
    return "none"


def resolve_voice_kind(engine: str, lang: str, pack_state: str,
                       runtime: bool = False, eleven_voice_id: str = ""
                       ) -> tuple[str, Optional[str]]:
    """세션 목소리 (kind, note) — C# VoicePolicy.ResolveKind와 같은 규칙.
    ko는 kokoro가 없어 edge, edge는 edge, elevenlabs는 voice_id가 있을 때만,
    그 밖(kokoro)은 팩(또는 런타임)이 있으면 kokoro, 없으면 세션 전체를 edge로."""
    engine = str(engine or "").lower()
    ko = str(lang or "").lower().startswith("ko")
    if ko and engine != "elevenlabs":
        return "edge", ("ko" if engine != "edge" else None)
    if engine == "edge":
        return "edge", None
    if engine == "elevenlabs":
        if eleven_voice_id:
            return "elevenlabs", None
        return "edge", "no-elevenlabs-voice"
    if pack_state == "ok" or runtime:
        return "kokoro", None
    return "edge", ("pack-incomplete" if pack_state == "incomplete" else "no-kokoro")


def kokoro_plan(cache_dir: str, voice: str, tone: str, text: str,
                exists=os.path.isfile):
    """팩에서 찾을 소리 — ("fx", 효과 통문장 경로) | ("seg", 세그먼트 경로들) |
    (None, 처음 빠진 문장 또는 None). 파일 확인만 한다 (exists로 가상 팩 대조)."""
    import radio_assemble
    import radiofx
    raw = _cache_path(cache_dir, f"kokoro|{voice}|{tone}|{text}", ext="wav")
    rfx = os.path.splitext(raw)[0] + f"_rfx{radiofx.VERSION}.wav"
    if exists(rfx):
        return "fx", rfx
    if not all(exists(os.path.join(cache_dir, n))
               for n in radio_assemble.ASSET_NAMES):
        return None, None
    paths, missing = radio_assemble.segment_lookup(cache_dir, voice, tone, text,
                                                   exists=exists)
    if paths is None:
        return None, missing
    return "seg", paths


class DropLog:
    """
    말하지 않은 콜 기록 — C# VoiceDropLog와 같은 규칙·문구. 건수는 모두 세고,
    로그는 텍스트별 처음 한 번만 (첫 50종까지).
    """

    LIMIT = 50

    def __init__(self):
        self._lock = threading.Lock()
        self._seen: set = set()
        self._cap_logged = False
        self.count = 0

    @property
    def distinct(self) -> int:
        with self._lock:
            return len(self._seen)

    def record(self, kind: str, reason: str, tone: Optional[str], text: str,
               urgent: bool = False) -> bool:
        """기록하고, 이번에 로그를 남겼으면 True."""
        cap_now = False
        with self._lock:
            self.count += 1
            if text in self._seen:
                return False
            if len(self._seen) >= self.LIMIT:
                if self._cap_logged:
                    return False
                self._cap_logged = True
                cap_now = True
            else:
                self._seen.add(text)
        if cap_now:
            log.info("[무음] 기록 상한 %d종 — 이후는 건수만 셉니다", self.LIMIT)
            return False
        line = (f"[무음] {kind} 목소리로 낼 수 없어 말하지 않음 ({reason}) "
                f"[{tone or 'casual'}] {text}")
        if urgent:
            log.warning("%s", line)
        else:
            log.info("%s", line)
        return True


class RecentCall:
    """최근 무전 한 줄 — dropped면 목소리를 섞지 않으려고 자막으로만 낸 콜."""

    __slots__ = ("stamp", "text", "dropped")

    def __init__(self, stamp: str, text: str, dropped: bool = False):
        self.stamp = stamp
        self.text = text
        self.dropped = dropped

    @property
    def line(self) -> str:
        return f"{self.stamp}  {self.text}"


class VoiceChain(TTSEngine):
    """
    세션 목소리 하나의 엔진 묶음. synth가 None이면 그 목소리로 낼 수 없는 콜 —
    호출부는 말하지 않는다 (miss_reason으로 기록). free_text는 LLM 같은 임의
    문장을 이 목소리로 말할 수 있는지 (voice.VoiceGenerator가 LLM 호출을 거른다).
    """

    MISS_REASON = {"kokoro": "kokoro-miss", "edge": "edge-failed",
                   "elevenlabs": "elevenlabs-miss"}

    def __init__(self, engine: TTSEngine, kind: str, note: Optional[str] = None,
                 allow_fallback: bool = False, runtime=None, api_key: str = "",
                 missing: int = -1):
        self.engine = engine
        self.kind = kind
        self.note = note
        self.allow_fallback = allow_fallback
        self.runtime = runtime
        self.api_key = api_key
        self.missing = missing

    @property
    def miss_reason(self) -> str:
        return self.MISS_REASON.get(self.kind, "miss")

    @property
    def free_text(self) -> bool:
        if self.kind == "edge":
            return True
        if self.allow_fallback:
            return True
        if self.kind == "kokoro":
            return self.runtime is not None and self.runtime.runtime_ready()
        if self.kind == "elevenlabs":
            return bool(self.api_key)
        return False

    def synth(self, text: str, tone: str = "casual") -> Optional[str]:
        return self.engine.synth(text, tone)


def _count_pack_missing(cache_dir: str, voice: str) -> int:
    """사전 캐시 대상 중 kokoro 통문장(효과본·원본)이 없는 수 — C# CountMissing(maxTier 0)."""
    import radiofx
    from voice import PhrasePool, iter_pregen_texts, lines_file
    n = 0
    for tone, text in set(iter_pregen_texts(PhrasePool(lines_file("en")))):
        raw = _cache_path(cache_dir, f"kokoro|{voice}|{tone}|{text}", ext="wav")
        rfx = os.path.splitext(raw)[0] + f"_rfx{radiofx.VERSION}.wav"
        if not (os.path.isfile(rfx) or os.path.isfile(raw)):
            n += 1
    return n


def build_engine(cfg) -> VoiceChain:
    """세션 목소리를 정하고 그 목소리만 쓰는 엔진을 만든다 (세션 중 다시 정하지 않음)."""
    cache_dir = cfg.get("tts.cache_dir", "audio_cache")
    engine = str(cfg.get("tts.engine", "edge")).lower()
    edge_voice = cfg.get("tts.edge_voice", "en-GB-RyanNeural")
    # 멘트 언어와 보이스 불일치 자동 보정 — 한국어 멘트를 영어 보이스로
    # 읽으면(또는 그 반대) 알아들을 수 없다
    lang = str(cfg.get("voice.language", "en")).lower()
    if lang.startswith("ko") and not edge_voice.startswith("ko"):
        log.info("멘트 언어 ko ↔ 보이스 %s 불일치 — ko-KR-InJoonNeural로 자동 전환",
                 edge_voice)
        edge_voice = "ko-KR-InJoonNeural"
    elif not lang.startswith("ko") and edge_voice.startswith("ko"):
        log.info("멘트 언어 en ↔ 보이스 %s 불일치 — en-GB-RyanNeural로 자동 전환",
                 edge_voice)
        edge_voice = "en-GB-RyanNeural"
    edge = EdgeTTSEngine(
        cache_dir,
        edge_voice,
        cfg.get("tts.edge_rate", "+10%"),
    )
    radio_fx = cfg.get("tts.radio_fx", True)
    noise = cfg.get("tts.radio_noise", 0.004)
    allow = bool(cfg.get("tts.allow_fallback", False))
    kokoro_voice = cfg.get("tts.kokoro_voice", "bm_george")
    eleven_id = cfg.get("tts.elevenlabs_voice_id", "")
    eleven_key = cfg.get("tts.elevenlabs_api_key", "")

    def maybe_fx(e: TTSEngine) -> TTSEngine:
        return RadioFXEngine(e, noise=noise) if radio_fx else e

    kk = None
    pack_state = "none"
    runtime = False
    if engine not in ("edge", "elevenlabs") and not lang.startswith("ko"):
        kk = KokoroEngine(cache_dir, kokoro_voice, allow_download=False)
        pack_state = kokoro_pack_state(cache_dir, kokoro_voice)
        runtime = kk.runtime_ready()
    kind, note = resolve_voice_kind(engine, lang, pack_state, runtime, eleven_id)

    missing = -1
    if kind == "kokoro":
        # 배포 팩(통문장 _rfx → 문장 조립) → 원본 kokoro(+효과, 런타임 합성 포함).
        # 팩은 효과본만 담고 있어 무전 효과 설정과 무관하게 효과를 켠다.
        tail: TTSEngine = ChainEngine([kk, edge]) if allow else kk
        built: TTSEngine = ChainEngine([
            KokoroPackEngine(cache_dir, kokoro_voice),
            RadioFXEngine(tail, noise=noise),
        ])
        if not runtime:
            try:
                missing = _count_pack_missing(cache_dir, kokoro_voice)
            except Exception:
                log.exception("음성 팩 누락 멘트 확인 실패")
            if missing > 0:
                log.warning("음성 팩에 없는 멘트 %d개 (통문장 %d, 문장 0) — 해당 콜은 자막만",
                            missing, missing)
    elif kind == "elevenlabs":
        # 키가 비어 있어도 캐시 전용으로 동작 (배포판 시나리오)
        eleven = ElevenLabsEngine(cache_dir, eleven_key, eleven_id)
        if not eleven_key:
            log.info("ElevenLabs 캐시 전용 모드 — 캐시에 없는 멘트는 %s",
                     "edge로" if allow else "자막만")
        built = maybe_fx(ChainEngine([eleven, edge]) if allow else eleven)
    else:
        built = maybe_fx(edge)
    log.info("목소리: %s%s%s", kind, f" ({note})" if note else "",
             " — 목소리 섞임 허용 (못 내는 멘트는 다른 목소리로)" if allow else "")
    return VoiceChain(built, kind, note, allow_fallback=allow, runtime=kk,
                      api_key=eleven_key, missing=missing)


def build_pregen_engine(cfg) -> TTSEngine:
    """
    사전 생성용 엔진. kokoro(en)는 팩(_rfx/조립)이나 edge로 건너뛰지 않고
    항상 kokoro로 렌더한다 — 배포 팩에 edge 음성이 섞이지 않게.
    그 밖의 엔진/언어는 build_engine과 같다.
    """
    engine = cfg.get("tts.engine", "edge")
    lang = str(cfg.get("voice.language", "en")).lower()
    if engine != "kokoro" or lang.startswith("ko"):
        return build_engine(cfg)
    built: TTSEngine = KokoroEngine(cfg.get("tts.cache_dir", "audio_cache"),
                                    cfg.get("tts.kokoro_voice", "bm_george"))
    if cfg.get("tts.radio_fx", True):
        built = RadioFXEngine(built, noise=cfg.get("tts.radio_noise", 0.004))
    return built


class AudioPlayer:
    """pygame.mixer 기반 재생. pygame이 없거나 오디오 장치가 없으면 무음 동작."""

    def __init__(self, volume: float = 0.9):
        self.volume = volume
        self._mixer = None
        self._init_failed = False

    def _ensure_mixer(self) -> bool:
        if self._mixer is not None:
            return True
        if self._init_failed:
            return False
        try:
            import pygame
            pygame.mixer.init()
            pygame.mixer.music.set_volume(self.volume)
            self._mixer = pygame.mixer
            return True
        except Exception as e:
            log.warning("오디오 초기화 실패 (텍스트 전용으로 동작): %s", e)
            self._init_failed = True
            return False

    def play(self, path: str, should_interrupt: Callable[[], bool],
             urgent: bool = False) -> bool:
        """재생 완료 시 True, 중단됐으면 False. urgent면 지연 없이 즉시."""
        if not self._ensure_mixer():
            return True
        # 미세 랜덤화: 기계적인 즉답 느낌 제거 (0~300ms 지연, 볼륨 ±5%).
        # 긴급 콜(나란히/충격 등)은 안전 정보라 지연 없이 바로 내보낸다.
        if not urgent:
            time.sleep(random.uniform(0.0, 0.3))
        try:
            self._mixer.music.set_volume(
                max(min(self.volume * random.uniform(0.95, 1.05), 1.0), 0.0))
            self._mixer.music.load(path)
            self._mixer.music.play()
            while self._mixer.music.get_busy():
                if should_interrupt():
                    self._mixer.music.stop()
                    log.debug("긴급 콜을 위해 재생 중단")
                    return False
                time.sleep(0.05)
            return True
        except Exception as e:
            log.warning("재생 실패: %s", e)
            return True

    def stop(self) -> None:
        if self._mixer is not None:
            try:
                self._mixer.music.stop()
            except Exception:
                pass


class SpeechLogger:
    """
    발화 로그 (JSONL): [타임스탬프, 이벤트, 멘트, 소스(캐시/LLM), 톤, 우선순위].
    리플레이 후 반복감/타이밍 검토용. data/speech_log.jsonl에 append.
    """

    def __init__(self, path: Optional[str]):
        self._file = None
        if path:
            os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
            self._file = open(path, "a", encoding="utf-8")
            log.info("발화 로그: %s", path)

    def write(self, ev, text: str, source: str, lap: int,
              spoken: bool = True, miss: Optional[str] = None) -> None:
        if self._file is None or self._file.closed:
            return
        entry = {
            "ts": time.strftime("%Y-%m-%dT%H:%M:%S"),
            "lap": lap,
            "event": ev.type,
            "priority": int(ev.priority),
            "tone": ev.tone,
            "source": source,
            "text": text,
        }
        if not spoken:
            # 세션 목소리로 낼 수 없어 말하지 않은 콜 (자막·최근 무전에만)
            entry["spoken"] = False
            entry["miss"] = miss
        self._file.write(json.dumps(entry, ensure_ascii=False) + "\n")
        self._file.flush()

    def close(self) -> None:
        if self._file is not None:
            self._file.close()


class VoiceWorker(threading.Thread):
    """
    이벤트 큐 소비 스레드: Event → 멘트 텍스트 → TTS → 재생.
    voice_gen은 voice.VoiceGenerator (텍스트가 None이면 발화 억제 — 침묵이 기본값).
    """

    def __init__(self, bus: EventBus, voice_gen, engine: TTSEngine,
                 player: AudioPlayer, state, enabled: bool = True,
                 speech_log: Optional[SpeechLogger] = None):
        super().__init__(name="voice-worker", daemon=True)
        self.bus = bus
        self.voice_gen = voice_gen
        self.engine = engine
        self.player = player
        self.state = state
        self.enabled = enabled
        self.speech_log = speech_log or SpeechLogger(None)
        self._stop_flag = threading.Event()
        # 최근 발화 RecentCall (SimHub 플러그인 상태 화면에 노출)
        self.recent: deque = deque(maxlen=5)
        self.drops = DropLog()

    def stop(self) -> None:
        self._stop_flag.set()
        self.player.stop()
        # 워커가 마지막 멘트를 마치기를 기다린 뒤 로그를 닫는다
        try:
            self.join(timeout=3.0)
        except RuntimeError:
            pass
        self.speech_log.close()

    def run(self) -> None:
        log.info("보이스 워커 시작 (음성 %s)", "켜짐" if self.enabled else "꺼짐")
        while not self._stop_flag.is_set():
            ev = self.bus.pop(timeout=0.5)
            if ev is None:
                continue
            try:
                self._speak(ev)
            except Exception:
                log.exception("멘트 처리 중 오류 (이벤트: %s)", ev.type)

    def _speak(self, ev) -> None:
        text, source = self.voice_gen.text_for(ev)
        if not text:
            return
        log.info("🎙️ [무전] %s", text)
        call = RecentCall(time.strftime('%H:%M:%S'), text)
        self.recent.append(call)
        lap = len(self.state.laps)
        self.state.add_narrative(f"(랩{lap}) 엔지니어: {text}")
        self._maybe_bridge(ev)
        if not self.enabled:
            self.speech_log.write(ev, text, source, lap=lap)
            return
        tone = getattr(ev, "tone", None) or "casual"
        path = self.engine.synth(text, tone)
        if path is None:
            # 세션 목소리로 낼 수 없다 — 다른 목소리로 대신 말하지 않는다
            call.dropped = True
            reason = getattr(self.engine, "miss_reason", "miss")
            self.drops.record(getattr(self.engine, "kind", "?"), reason, tone, text,
                              urgent=(ev.priority == Priority.CRITICAL))
            self.speech_log.write(ev, text, source, lap=lap, spoken=False, miss=reason)
            return
        self.speech_log.write(ev, text, source, lap=lap)
        # CRITICAL 재생 중엔 끼어들 수 없음. 그 외엔 긴급 대기 이벤트가 생기면 중단.
        if ev.priority == Priority.CRITICAL:
            should_interrupt = lambda: False
        else:
            should_interrupt = self.bus.urgent_pending.is_set
        self.player.play(path, should_interrupt,
                         urgent=(ev.priority == Priority.CRITICAL))

    def _maybe_bridge(self, ev) -> None:
        """
        브리지 기법: 긴급 콜(t=0, 캐시) 직후 LLM 후속 멘트(t+3~5s)를
        백그라운드 스레드에서 생성해 낮은 우선순위로 큐에 넣는다.
        생성이 끝났을 때 상황이 이미 종료됐으면(valid_fn) 폐기된다.
        """
        # 세션 목소리가 임의 문장을 말할 수 없으면 LLM을 부르지 않는다 (llm_speakable)
        if not ev.bridge or not getattr(self.voice_gen, "llm_speakable", False):
            return

        def worker(src_ev):
            try:
                text = self.voice_gen.bridge_text(src_ev)
            except Exception:
                log.exception("브리지 멘트 생성 실패")
                return
            if not text:
                return
            if src_ev.valid_fn is not None:
                try:
                    if not src_ev.valid_fn():
                        log.debug("브리지 폐기 (상황 종료): %s", src_ev.type)
                        return
                except Exception:
                    pass
            self.bus.push(Event(
                type=EventType.BRIDGE_FOLLOWUP, priority=Priority.NORMAL,
                message=text, ttl=8.0,
                dedup_key=f"bridge_{src_ev.key}",
                valid_fn=src_ev.valid_fn,
            ))

        threading.Thread(target=worker, args=(ev,),
                         name="bridge-llm", daemon=True).start()
