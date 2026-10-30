"""
teamradio56 (LMU 레이스 엔지니어 무전) — 메인 루프.

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

import procguard
from config import load_config, Config
from messages import set_language
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
from analyzers.reporter import StatusReporter
from training import LapCoach, TrackHistory, Debriefer
from briefing import compose_session_briefing
from resttelemetry import RestTelemetry
from status import StatusWriter, recent_call_fields
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
                 recorder: SnapshotRecorder | None = None,
                 status_path: str | None = None):
        self.cfg = cfg
        self.source = source
        self.recorder = recorder
        # SimHub 플러그인 엔진 모드: 상태를 파일로 내보내 UI가 표시
        self.status = StatusWriter(status_path)
        self.poll_interval = 1.0 / max(cfg.get("app.poll_hz", 5), 1)
        self.status_interval = cfg.get("app.console_status_sec", 1.0)
        self._last_status = 0.0
        self._last_waiting_msg = 0.0
        self._was_in_session = False
        self._briefed_session = False   # 세션 시작 브리핑을 이미 했는가
        self._running = True
        self._silence = False           # 종료 처리 중 발화 중단 (양보/부모 종료)

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
        self.reporter = StatusReporter(cfg)   # HUD 대체 정기 무전 (기본 꺼짐)
        self.coach = LapCoach()
        self.history = TrackHistory(cfg.get("app.data_dir", "data"))
        self.debriefer = Debriefer()

        # LMU 내장 REST API 보조 소스 — 공유 메모리에 없는 정보(가상 에너지/
        # 날씨 예보/피트 전략)를 저주파로 보충. 미지원 환경이면 자동 비활성.
        # 리플레이 모드에선 의미 없으므로 시작하지 않는다.
        # TODO(REST): tools/probe_rest.py로 실제 응답 확보 후 fuel(가상 에너지),
        #             strategy(날씨 예보), 피트 전 브리핑(피트 전략)에 연결.
        self.rest = RestTelemetry(cfg)
        if not isinstance(source, ReplayTelemetry):
            self.rest.start()
        # 세션 목소리를 먼저 정한다 — LLM 멘트는 그 목소리가 임의 문장을 말할 수
        # 있을 때만 (음성 꺼짐이면 텍스트 전용이라 제한 없음)
        engine = build_engine(cfg)
        self.engine = engine
        self.voice_gen = VoiceGenerator(
            cfg, self.state,
            speakable=lambda: not cfg.get("voice.enabled", True) or engine.free_text)
        if self.voice_gen.llm.available and not engine.free_text \
                and cfg.get("voice.enabled", True):
            log.info("LLM 멘트: %s 목소리로 임의 문장을 말할 수 없어 호출하지 않음 — 템플릿 사용",
                     engine.kind)
        if engine.kind == "kokoro":
            self._warn_thresholds_below_pack(cfg)
        speech_log_path = None
        if cfg.get("app.speech_log", True):
            speech_log_path = os.path.join(cfg.get("app.data_dir", "data"),
                                           "speech_log.jsonl")
        self.worker = VoiceWorker(
            bus=self.bus,
            voice_gen=self.voice_gen,
            engine=engine,
            player=AudioPlayer(cfg.get("voice.volume", 0.9)),
            state=self.state,
            enabled=cfg.get("voice.enabled", True),
            speech_log=SpeechLogger(speech_log_path),
        )
        self.worker.start()

    # -- 메인 루프 ----------------------------------------------------------

    def run(self) -> None:
        log.info("teamradio56 시작 (폴링 %.0fHz)", 1.0 / self.poll_interval)
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
            self._publish_status(None, "게임 대기 중")
            return

        # 세션 시작/종료 전이 감지
        if self._was_in_session and not snap.in_session:
            self._on_session_end("세션 종료")
        elif not self._was_in_session and snap.in_session:
            log.info("세션 시작 감지 (트랙: %s)", snap.session.get("track", "?"))
            self._briefed_session = False
        self._was_in_session = snap.in_session

        if not snap.in_session:
            if now - self._last_waiting_msg > 5.0:
                log.info("세션 대기 중... (게임 연결됨, 세션 없음)")
                self._last_waiting_msg = now
            self._publish_status(snap, "세션 대기 중")
            return

        # 모니터/가라지/메뉴(mInRealtime=false)에서는 분석·발화 중단.
        # LMU가 이 필드를 이상하게 채우면 config에서 require_realtime: false
        if self.cfg.get("app.require_realtime", True) \
                and not snap.session.get("in_realtime", True):
            if now - self._last_waiting_msg > 5.0:
                log.info("모니터/메뉴 상태 — 주행 복귀 대기 중")
                self._last_waiting_msg = now
            self._publish_status(snap, "모니터/메뉴 — 주행 복귀 대기")
            return

        self.on_snapshot(snap)

        self._publish_status(snap, "주행 중")

        if now - self._last_status >= self.status_interval:
            self._last_status = now
            self.print_status(snap)

    def _publish_status(self, snap, state_text: str) -> None:
        """SimHub 플러그인 UI가 읽을 상태 파일 갱신 (엔진 모드에서만 동작)."""
        if not self.status.path:
            return
        fields = {"running": True, "state": state_text,
                  "connected": snap is not None,
                  # 오버레이/프로퍼티용: 진행 중 이슈 키 목록 (damage,fuel,...)
                  "issues": ",".join(self.state.issues.keys())}
        me = snap.player_scoring() if snap is not None else None
        if snap is not None and me is not None:
            fields.update({
                "track": snap.session.get("track", ""),
                "phase": GAME_PHASES.get(snap.session.get("game_phase", -1), "?"),
                "place": me["place"],
                "class_place": self.state.class_place_of(snap, me),
                "cls": me["cls"],
                "lap": me["total_laps"],
                "vehicles": snap.session.get("num_vehicles", 0),
                "fuel": (snap.player or {}).get("fuel", 0.0),
                "speed": (snap.player or {}).get("speed_kmh", 0.0),
            })
        fields.update(recent_call_fields(self.worker.recent))
        fields.update({
            "voice": self.engine.kind,
            "voice_note": self.engine.note or "",
            "voice_dropped": self.worker.drops.count,
            "voice_missing": self.engine.missing,
            "llm": self.voice_gen.llm.available,
            "llm_voice": self.engine.free_text,
        })
        self.status.write(fields)

    # 음성 팩 문장의 하한 — 설정 임계값이 이보다 낮으면 그 아래 값은 팩에 없다
    PACK_MIN_THRESHOLDS = (
        ("pace_delta_sec", 0.7),
        ("rival_pace_diff", 0.3),
        ("tyre_temp_imbalance", 12),
        ("brake_temp_warn", 695),
        ("fuel_save_delta", 0.05),
    )

    @classmethod
    def _warn_thresholds_below_pack(cls, cfg) -> None:
        for key, minimum in cls.PACK_MIN_THRESHOLDS:
            v = cfg.get(f"thresholds.{key}")
            try:
                low = v is not None and float(v) < minimum
            except (TypeError, ValueError):
                low = False
            if low:
                log.warning("설정 thresholds.%s=%s가 음성 팩 하한 %s보다 낮음 — 그 아래 값은 자막만",
                            key, v, minimum)

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
                         self.health, self.strategy, self.history, self.pace,
                         self.reporter):
            reset = getattr(analyzer, "reset", None)
            if reset:
                reset()

    # -- 훅 (이후 마일스톤에서 확장) -----------------------------------------

    def on_snapshot(self, snap: Snapshot) -> None:
        """5Hz마다 호출. 긴급 이벤트만 체크하고, 랩 완료 시 무거운 분석."""
        self._maybe_session_briefing(snap)
        self.traffic.on_tick(self.state, snap, self.bus)
        self.racecontrol.on_tick(self.state, snap, self.bus)   # FCY/리미터/마일스톤
        self.rivals.on_tick(self.state, snap, self.bus)        # 경쟁자 피트 진입
        self.health.on_tick(self.state, snap, self.bus)        # 충격/부품 탈락
        lap = self.state.update(snap)
        if lap is not None:
            self.on_lap_complete(snap, lap)

    def _maybe_session_briefing(self, snap: Snapshot) -> None:
        """
        세션 시작 브리핑 (세션당 1회) — 주행 가능 상태에서 첫 데이터가 잡히면
        한 번 브리핑한다. 문장 조립은 briefing.compose_session_briefing.
        """
        if self._briefed_session:
            return
        if snap.player_scoring() is None:
            return
        self._briefed_session = True
        # 임의 문장을 못 말하는 목소리(kokoro 팩)면 목록에 없는 트랙 이름은 뺀다 (LLM 게이트와 같은 판단)
        pack_only = bool(self.cfg.get("voice.enabled", True)) and not self.engine.free_text
        text = compose_session_briefing(
            snap, self.state, self.cfg.get("thresholds.start_spotter_sec", 45),
            pack_only=pack_only)
        if text is None:
            return
        self.bus.push(Event(
            type=EventType.SESSION_BRIEFING, priority=Priority.NORMAL,
            message=text, dedup_key="session_brief",
            ttl=60.0, tone="casual",
        ))

    def on_lap_complete(self, snap: Snapshot, lap) -> None:
        """판단 로직의 90%는 여기서: 연료/페이스 분석 → 이벤트."""
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

        # HUD 대체 정기 무전 (reports 설정으로 켜짐)
        self.reporter.on_lap(self.state, snap, self.bus, fuel_status, tyre_status)

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

    def stop(self, silence: bool = False) -> None:
        """
        다른 스레드에서 정상 종료 요청 — 메인 루프가 다음 틱에 빠져나간다.
        silence=True(플러그인에 양보/부모 종료)면 종료 처리 동안에도 더
        말하지 않는다 — 대기 중인 콜이 다른 엔진 목소리와 겹치지 않게.
        """
        if silence:
            self._silence = True
        self._running = False

    def shutdown(self) -> None:
        self._running = False
        worker_stopped = False
        if self._silence:
            self.worker.stop()
            worker_stopped = True
        # 디브리핑은 워커 정지 전에 (LLM 1회, 텍스트는 파일로도 저장)
        try:
            self.debriefer.run(self.state, self.voice_gen.llm,
                               self.cfg.get("app.data_dir", "data"))
        except Exception:
            log.exception("디브리핑 생성 실패")
        if not worker_stopped:
            self.worker.stop()
        self.rest.stop()
        self.status.close()
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
    parser = argparse.ArgumentParser(description="teamradio56 — LMU 레이스 엔지니어 무전")
    parser.add_argument("--config", default="config.yaml", help="설정 파일 경로")
    parser.add_argument("--replay", metavar="PATH", help="녹화된 텔레메트리 JSONL 재생 (mock 모드)")
    parser.add_argument("--speed", type=float, default=1.0, help="리플레이 배속 (기본 1.0)")
    parser.add_argument("--record", metavar="PATH", help="텔레메트리를 JSONL로 녹화")
    parser.add_argument("--settings", metavar="PATH",
                        help="SimHub 플러그인 설정 파일 (엔진 모드에서 UI 값 수신)")
    parser.add_argument("--status-file", metavar="PATH",
                        help="엔진 상태를 이 파일에 기록 (플러그인 UI가 표시)")
    parser.add_argument("--parent-pid", type=int, default=0,
                        help="이 PID(SimHub)가 끝나면 엔진도 종료 (고아 엔진 방지)")
    args = parser.parse_args()

    # SimHub 엔진 모드에선 작업 디렉토리가 python.exe 위치(venv Scripts)로
    # 잡혀 온다 — config.yaml/audio_cache/data 같은 상대 경로가 전부
    # 깨지므로, 명시된 경로 인자만 절대 경로로 고정한 뒤 앱 기준
    # 디렉토리(이 파일 위치)로 이동한다.
    for name in ("config", "replay", "record", "settings", "status_file"):
        value = getattr(args, name, None)
        if value and value != parser.get_default(name):
            setattr(args, name, os.path.abspath(value))
    app_dir = (getattr(sys, "_MEIPASS", None)
               or os.path.dirname(os.path.abspath(__file__)))
    os.chdir(app_dir)

    # Windows 콘솔(cp949)에서 이모지/한글 로그가 인코딩 에러 내지 않게
    if sys.platform == "win32":
        try:
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, OSError):
            pass

    cfg = load_config(args.config, plugin_settings=args.settings)
    set_language(cfg.get("voice.language", "en"))   # 발화 문장 언어 (en/ko)
    logging.basicConfig(
        level=getattr(logging, cfg.get("app.log_level", "INFO").upper(), logging.INFO),
        format="%(asctime)s %(levelname)-5s %(name)-10s %(message)s",
        datefmt="%H:%M:%S",
    )

    # load_config는 로깅 설정 전에 돌아 로그가 묻히므로 여기서 한 번 더 알린다
    if args.settings:
        log.info("SimHub 엔진 모드 — 설정 파일: %s", args.settings)

    # 독립 실행(launch_with_lmu.bat 등)인데 SimHub 플러그인이 이미 떠 있으면
    # 물러난다 — 둘 다 돌면 모든 콜이 서로 다른 목소리로 겹쳐 나온다
    standalone = not args.settings and not args.replay
    if standalone and procguard.plugin_active():
        log.warning("SimHub teamradio56 플러그인이 실행 중 — 독립 실행 엔진은 "
                    "시작하지 않습니다 (플러그인이 무전을 담당)")
        return 0

    if args.record:
        os.makedirs(os.path.dirname(args.record) or ".", exist_ok=True)

    try:
        source, recorder = build_source(args)
    except RuntimeError as e:
        log.error("%s", e)
        return 1

    app = CrewChiefApp(cfg, source, recorder,
                       status_path=getattr(args, "status_file", None))
    parent_pid = args.parent_pid or procguard.parent_pid_from_env()
    if parent_pid:
        procguard.watch_parent(parent_pid, lambda: app.stop(silence=True))
    if standalone:
        procguard.watch_plugin(lambda: app.stop(silence=True))
    app.run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
