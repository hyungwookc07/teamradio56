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
import threading
import time
import urllib.request
from typing import Callable, Optional

from events import EventBus, Priority

log = logging.getLogger("tts")


def _cache_path(cache_dir: str, key: str, ext: str = "mp3") -> str:
    h = hashlib.md5(key.encode("utf-8")).hexdigest()[:20]
    return os.path.join(cache_dir, f"{h}.{ext}")


class TTSEngine:
    """text → 오디오 파일 경로. 실패 시 None (텍스트 로그로 폴백)."""

    def synth(self, text: str) -> Optional[str]:
        raise NotImplementedError


class NullTTSEngine(TTSEngine):
    def synth(self, text: str) -> Optional[str]:
        return None


class EdgeTTSEngine(TTSEngine):
    def __init__(self, cache_dir: str, voice: str, rate: str):
        self.cache_dir = cache_dir
        self.voice = voice
        self.rate = rate
        os.makedirs(cache_dir, exist_ok=True)

    def synth(self, text: str) -> Optional[str]:
        path = _cache_path(self.cache_dir, f"edge|{self.voice}|{self.rate}|{text}")
        if os.path.exists(path):
            return path
        try:
            import edge_tts
        except ImportError:
            log.warning("edge-tts 미설치 — 텍스트 출력만 합니다 (pip install edge-tts)")
            return None
        try:
            comm = edge_tts.Communicate(text, self.voice, rate=self.rate)
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

    def synth(self, text: str) -> Optional[str]:
        path = _cache_path(self.cache_dir, f"11labs|{self.voice_id}|{text}")
        if os.path.exists(path):
            return path
        body = json.dumps({
            "text": text,
            "model_id": "eleven_multilingual_v2",
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


def build_engine(cfg) -> TTSEngine:
    cache_dir = cfg.get("tts.cache_dir", "audio_cache")
    engine = cfg.get("tts.engine", "edge")
    if engine == "elevenlabs":
        key = cfg.get("tts.elevenlabs_api_key", "")
        vid = cfg.get("tts.elevenlabs_voice_id", "")
        if key and vid:
            return ElevenLabsEngine(cache_dir, key, vid)
        log.warning("elevenlabs 설정 미비 — edge로 폴백")
    return EdgeTTSEngine(
        cache_dir,
        cfg.get("tts.edge_voice", "ko-KR-InJoonNeural"),
        cfg.get("tts.edge_rate", "+10%"),
    )


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

    def play(self, path: str, should_interrupt: Callable[[], bool]) -> bool:
        """재생 완료 시 True, 중단됐으면 False."""
        if not self._ensure_mixer():
            return True
        try:
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


class VoiceWorker(threading.Thread):
    """
    이벤트 큐 소비 스레드: Event → 멘트 텍스트 → TTS → 재생.
    voice_gen은 voice.VoiceGenerator (텍스트가 None이면 발화 억제).
    """

    def __init__(self, bus: EventBus, voice_gen, engine: TTSEngine,
                 player: AudioPlayer, state, enabled: bool = True):
        super().__init__(name="voice-worker", daemon=True)
        self.bus = bus
        self.voice_gen = voice_gen
        self.engine = engine
        self.player = player
        self.state = state
        self.enabled = enabled
        self._stop_flag = threading.Event()

    def stop(self) -> None:
        self._stop_flag.set()
        self.player.stop()

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
        text = self.voice_gen.text_for(ev)
        if not text:
            return
        log.info("🎙️ [크루치프] %s", text)
        self.state.add_narrative(f"(랩{len(self.state.laps)}) 크루치프: {text}")
        if not self.enabled:
            return
        path = self.engine.synth(text)
        if path is None:
            return
        # CRITICAL 재생 중엔 끼어들 수 없음. 그 외엔 긴급 대기 이벤트가 생기면 중단.
        if ev.priority == Priority.CRITICAL:
            should_interrupt = lambda: False
        else:
            should_interrupt = self.bus.urgent_pending.is_set
        self.player.play(path, should_interrupt)
