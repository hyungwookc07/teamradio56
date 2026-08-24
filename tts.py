"""
TTS 합성 + 재생 스레드.

메인 폴링 루프를 절대 블로킹하지 않도록 합성/재생은 전부 VoiceWorker
스레드에서 처리한다. CRITICAL 이벤트가 대기 중이면 재생 중인 저우선순위
멘트를 중단하고 끼어든다.

엔진은 교체 가능: edge (기본, 무료) / elevenlabs. 의존성이 없거나 합성이
실패해도 앱은 죽지 않고 콘솔 텍스트로만 동작한다. (GPU 로컬 TTS는 게임과
GPU를 경쟁하므로 지원하지 않는다.)
"""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import os
import random
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
        # 캐시에 없는 멘트는 None → ChainEngine이 edge-tts로 폴백.
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


class ChainEngine(TTSEngine):
    """
    앞 엔진이 실패하면 다음 엔진으로. 배포 시나리오의 핵심:
    [ElevenLabs(캐시 전용도 가능), EdgeTTS] 순으로 걸면 미리 생성해 넣어둔
    프리미엄 오디오를 우선 쓰고, 캐시에 없는 즉석 문장(LLM 멘트 등)만
    무료 edge-tts로 낸다.
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


def build_engine(cfg) -> TTSEngine:
    cache_dir = cfg.get("tts.cache_dir", "audio_cache")
    engine = cfg.get("tts.engine", "edge")
    edge = EdgeTTSEngine(
        cache_dir,
        cfg.get("tts.edge_voice", "en-GB-RyanNeural"),
        cfg.get("tts.edge_rate", "+10%"),
    )
    built: TTSEngine = edge
    if engine == "elevenlabs":
        voice_id = cfg.get("tts.elevenlabs_voice_id", "")
        if voice_id:
            # 키가 비어 있어도 캐시 전용으로 앞에 세운다 (배포판 시나리오)
            eleven = ElevenLabsEngine(
                cache_dir, cfg.get("tts.elevenlabs_api_key", ""), voice_id)
            if not cfg.get("tts.elevenlabs_api_key", ""):
                log.info("ElevenLabs 캐시 전용 모드 — 캐시에 없는 멘트는 edge로 폴백")
            built = ChainEngine([eleven, edge])
        else:
            log.warning("elevenlabs voice_id 미설정 — edge로 폴백")
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

    def write(self, ev, text: str, source: str, lap: int) -> None:
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
        # 최근 발화 (SimHub 플러그인 상태 화면에 노출)
        self.recent: deque = deque(maxlen=5)

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
        log.info("🎙️ [크루치프] %s", text)
        self.recent.append(f"{time.strftime('%H:%M:%S')}  {text}")
        self.state.add_narrative(f"(랩{len(self.state.laps)}) 크루치프: {text}")
        self.speech_log.write(ev, text, source, lap=len(self.state.laps))
        self._maybe_bridge(ev)
        if not self.enabled:
            return
        path = self.engine.synth(text, getattr(ev, "tone", None) or "casual")
        if path is None:
            return
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
        if not ev.bridge or not getattr(self.voice_gen, "llm", None) \
                or not self.voice_gen.llm.available:
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
