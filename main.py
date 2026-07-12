"""
LMU AI 크루치프 — 메인 루프.

5Hz로 공유 메모리를 폴링하고, 랩 완료 시 분석기(연료/페이스)를 돌려
이벤트 큐에 넣는다. 멘트 생성/TTS는 보이스 워커 스레드가 처리하므로
이 루프는 절대 블로킹되지 않는다. 게임이 꺼져 있으면 대기 후 자동 재연결.

사용법:
    python main.py                          # 실전 (Windows, 게임 + 플러그인 필요)
    python main.py --record data/race.jsonl # 실전 + 텔레메트리 녹화
    python main.py --replay data/race.jsonl # 녹화 파일 재생 (게임 불필요)
    python main.py --replay data/race.jsonl --speed 10
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import time

from config import load_config, Config
from telemetry import (
    TelemetrySource,
    SharedMemoryTelemetry,
    ReplayTelemetry,
    SnapshotRecorder,
    Snapshot,
)
from state import SessionState
from events import EventBus, Event, EventType, Priority
from analyzers.fuel import FuelAnalyzer
from analyzers.pace import PaceAnalyzer
from analyzers.traffic import TrafficAnalyzer
from analyzers.tyres import TyreAnalyzer
from analyzers.strategy import StrategyEngine
from analyzers.racecontrol import RaceControlAnalyzer
from analyzers.rivals import RivalAnalyzer
from analyzers.health import HealthAnalyzer
from training import LapCoach, TrackHistory, Debriefer
from voice import VoiceGenerator
from tts import AudioPlayer, SpeechLogger, VoiceWorker, build_engine

log = logging.getLogger("main")

GAME_PHASES = {
    0: "차고", 1: "웜업", 2: "그리드워크", 3: "포메이션", 4: "카운트다운",
    5: "그린플래그", 6: "풀코스옐로", 7: "세션중단", 8: "세션종료", 9: "일시정지",
}


def fmt_time(sec: float) -> str:
    if sec is None or sec <= 0:
        return "-:--.---"
    m, s = divmod(sec, 60)
    return f"{int(m)}:{s:06.3f}"


def fmt_gap(sec: float) -> str:
    if sec is None or sec <= 0:
        return "----"
    return f"{sec:+.1f}s" if sec < 600 else "----"


class CrewChiefApp:
    def __init__(self, cfg: Config, source: TelemetrySource,
                 recorder: SnapshotRecorder | None = None):
        self.cfg = cfg
        self.source = source
        self.recorder = recorder
        self.poll_interval = 1.0 / max(cfg.get("app.poll_hz", 5), 1)
        self.status_interval = cfg.get("app.console_status_sec", 1.0)
        self._last_status = 0.0
        self._last_waiting_msg = 0.0
        self._was_in_session = False
        self._running = True

        # 상태 + 분석기 + 이벤트 버스 + 보이스 워커
        self.state = SessionState()
        if cfg.get("app.save_race_json", True):
            self.state.autosave_dir = cfg.get("app.data_dir", "data")
        self.bus = EventBus(cfg["cooldowns"])
        self.fuel = FuelAnalyzer(cfg)
        self.pace = PaceAnalyzer(cfg)
        self.traffic = TrafficAnalyzer(cfg)
        self.tyres = TyreAnalyzer(cfg)
        self.strategy = StrategyEngine(cfg)
        self.racecontrol = RaceControlAnalyzer(cfg)
        self.rivals = RivalAnalyzer(cfg)
        self.health = HealthAnalyzer(cfg)
        self.coach = LapCoach()
        self.history = TrackHistory(cfg.get("app.data_dir", "data"))
        self.debriefer = Debriefer()
        self.voice_gen = VoiceGenerator(cfg, self.state)
        speech_log_path = None
        if cfg.get("app.speech_log", True):
            speech_log_path = os.path.join(cfg.get("app.data_dir", "data"),
                                           "speech_log.jsonl")
        self.worker = VoiceWorker(
            bus=self.bus,
            voice_gen=self.voice_gen,
            engine=build_engine(cfg),
            player=AudioPlayer(cfg.get("voice.volume", 0.9)),
            state=self.state,
            enabled=cfg.get("voice.enabled", True),
            speech_log=SpeechLogger(speech_log_path),
        )
        self.worker.start()

    # -- 메인 루프 ----------------------------------------------------------

    def run(self) -> None:
        log.info("LMU AI 크루치프 시작 (폴링 %.0fHz)", 1.0 / self.poll_interval)
        try:
            while self._running:
                cycle_start = time.monotonic()
                self._tick()
                if isinstance(self.source, ReplayTelemetry) and self.source.finished:
                    log.info("리플레이 종료")
                    break
                # 폴링 주기 유지 (처리 시간 보정)
                elapsed = time.monotonic() - cycle_start
                time.sleep(max(self.poll_interval - elapsed, 0.0))
        except KeyboardInterrupt:
            log.info("종료 요청 (Ctrl+C)")
        finally:
            self.shutdown()

    def _tick(self) -> None:
        snap = self.source.poll()
        if snap is None:
            return
        if self.recorder is not None and snap.connected:
            self.recorder.write(snap)

        now = time.monotonic()
        if not snap.connected:
            # 게임이 내려갔으면 진행 중이던 세션도 종료 처리
            if self._was_in_session:
                self._on_session_end("게임 연결 끊김")
                self._was_in_session = False
            if now - self._last_waiting_msg > 5.0:
                log.info("게임 대기 중... (LMU 실행 + 공유 메모리 플러그인 활성화 필요)")
                self._last_waiting_msg = now
            return

        # 세션 시작/종료 전이 감지
        if self._was_in_session and not snap.in_session:
            self._on_session_end("세션 종료")
        elif not self._was_in_session and snap.in_session:
            log.info("세션 시작 감지 (트랙: %s)", snap.session.get("track", "?"))
        self._was_in_session = snap.in_session

        if not snap.in_session:
            if now - self._last_waiting_msg > 5.0:
                log.info("세션 대기 중... (게임 연결됨, 세션 없음)")
                self._last_waiting_msg = now
            return

        # 모니터/가라지/메뉴(mInRealtime=false)에서는 분석·발화 중단.
        # LMU가 이 필드를 이상하게 채우면 config에서 require_realtime: false
        if self.cfg.get("app.require_realtime", True) \
                and not snap.session.get("in_realtime", True):
            if now - self._last_waiting_msg > 5.0:
                log.info("모니터/메뉴 상태 — 주행 복귀 대기 중")
                self._last_waiting_msg = now
            return

        self.on_snapshot(snap)

        if now - self._last_status >= self.status_interval:
            self._last_status = now
            self.print_status(snap)

    def _on_session_end(self, reason: str) -> None:
        """세션 종료: 디브리핑 + 히스토리 저장 + 전 분석기 상태 리셋."""
        log.info("%s 감지 → 세션 마무리 (디브리핑/저장/리셋)", reason)
        try:
            self.debriefer.run(self.state, self.voice_gen.llm,
                               self.cfg.get("app.data_dir", "data"))
        except Exception:
            log.exception("디브리핑 생성 실패")
        if self.cfg.get("app.save_race_json", True):
            self.state.save_json(self.cfg.get("app.data_dir", "data"))
        self.state.reset()
        self.bus.clear()
        for analyzer in (self.traffic, self.racecontrol, self.rivals,
                         self.health, self.strategy, self.history):
            reset = getattr(analyzer, "reset", None)
            if reset:
                reset()

    # -- 훅 (이후 마일스톤에서 확장) -----------------------------------------

    def on_snapshot(self, snap: Snapshot) -> None:
        """5Hz마다 호출. 긴급 이벤트만 체크하고, 랩 완료 시 무거운 분석."""
        self.traffic.on_tick(self.state, snap, self.bus)
        self.racecontrol.on_tick(self.state, snap, self.bus)   # FCY/리미터/마일스톤
        self.rivals.on_tick(self.state, snap, self.bus)        # 경쟁자 피트 진입
        self.health.on_tick(self.state, snap, self.bus)        # 충격/부품 탈락
        lap = self.state.update(snap)
        if lap is not None:
            self.on_lap_complete(snap, lap)

    def on_lap_complete(self, snap: Snapshot, lap) -> None:
        """크루치프 로직의 90%는 여기서: 연료/페이스 분석 → 이벤트."""
        fuel_status = self.fuel.on_lap(self.state, snap, self.bus)
        self.pace.on_lap(self.state, snap, self.bus, lap)
        tyre_status = self.tyres.on_lap(self.state, snap, self.bus)
        if fuel_status:
            log.debug("연료: %s", fuel_status)

        # 피트 아웃랩 다음 랩 → 스틴트 브리핑 (LLM)
        if lap.in_pits and self.state.is_race:
            self.bus.push(Event(
                type=EventType.STINT_BRIEFING, priority=Priority.NORMAL,
                data={}, dedup_key=f"stint_{lap.lap_number}",
            ))
        # 전략 엔진: 판단이 필요한 전이 시점에만 LLM 전략 멘트 트리거
        self.strategy.on_lap(self.state, snap, self.bus, fuel_status, tyre_status)

        # 클래스 순위 변동 / 라이벌 페이스 인텔 / 차량 컨디션
        self.racecontrol.on_lap(self.state, snap, self.bus)
        self.rivals.on_lap(self.state, snap, self.bus)
        self.health.on_lap(self.state, snap, self.bus)

        # 트레이닝: 섹터 델타 피드백 + 과거 세션 대비 추세 (한 번만)
        self.coach.on_lap(self.state, self.bus)
        self.history.on_lap(self.state, self.bus)

    # -- 콘솔 상태 출력 ------------------------------------------------------

    def print_status(self, snap: Snapshot) -> None:
        me = snap.player_scoring()
        if me is None:
            return
        p = snap.player
        ses = snap.session

        gap_ahead = me["time_behind_next"] if me["place"] > 1 else None
        gap_behind = None
        for v in snap.vehicles:
            if v["place"] == me["place"] + 1:
                gap_behind = v["time_behind_next"]
                break

        phase = GAME_PHASES.get(ses["game_phase"], f'?{ses["game_phase"]}')
        fuel = p.get("fuel")
        fuel_str = f"{fuel:.1f}L" if fuel is not None else "--"
        class_p = SessionState.class_place_of(snap, me)
        log.info(
            "[%s|%s] 클래스P%d(전체P%d) L%d | 연료 %s | 랩 %s (베스트 %s) | 앞 %s / 뒤 %s | %.0fkm/h",
            ses["track"][:20] or "?", phase,
            class_p, me["place"], me["total_laps"] + 1,
            fuel_str,
            fmt_time(me["last_lap"]), fmt_time(me["best_lap"]),
            fmt_gap(gap_ahead), fmt_gap(gap_behind),
            p.get("speed_kmh", 0.0),
        )

    def shutdown(self) -> None:
        self._running = False
        # 디브리핑은 워커 정지 전에 (LLM 1회, 텍스트는 파일로도 저장)
        try:
            self.debriefer.run(self.state, self.voice_gen.llm,
                               self.cfg.get("app.data_dir", "data"))
        except Exception:
            log.exception("디브리핑 생성 실패")
        self.worker.stop()
        if self.cfg.get("app.save_race_json", True):
            self.state.save_json(self.cfg.get("app.data_dir", "data"))
        if self.recorder is not None:
            self.recorder.close()
        self.source.close()
        log.info("정리 완료")


def build_source(args) -> tuple[TelemetrySource, SnapshotRecorder | None]:
    if args.replay:
        return ReplayTelemetry(args.replay, speed=args.speed), None
    source = SharedMemoryTelemetry()
    recorder = SnapshotRecorder(args.record) if args.record else None
    return source, recorder


def main() -> int:
    parser = argparse.ArgumentParser(description="LMU AI 크루치프")
    parser.add_argument("--config", default="config.yaml", help="설정 파일 경로")
    parser.add_argument("--replay", metavar="PATH", help="녹화된 텔레메트리 JSONL 재생 (mock 모드)")
    parser.add_argument("--speed", type=float, default=1.0, help="리플레이 배속 (기본 1.0)")
    parser.add_argument("--record", metavar="PATH", help="텔레메트리를 JSONL로 녹화")
    args = parser.parse_args()

    # Windows 콘솔(cp949)에서 이모지/한글 로그가 인코딩 에러 내지 않게
    if sys.platform == "win32":
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, OSError):
            pass

    cfg = load_config(args.config)
    logging.basicConfig(
        level=getattr(logging, cfg.get("app.log_level", "INFO").upper(), logging.INFO),
        format="%(asctime)s %(levelname)-5s %(name)-10s %(message)s",
        datefmt="%H:%M:%S",
    )

    if args.record:
        os.makedirs(os.path.dirname(args.record) or ".", exist_ok=True)

    try:
        source, recorder = build_source(args)
    except RuntimeError as e:
        log.error("%s", e)
        return 1

    app = CrewChiefApp(cfg, source, recorder)
    app.run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
