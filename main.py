"""
LMU AI 크루치프 — 메인 루프.

v0.1: 5Hz로 공유 메모리를 폴링해서 연료/랩타임/갭을 1초마다 콘솔에 출력.
게임이 꺼져 있으면 크래시 없이 대기하다가 자동 재연결.

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
        self._running = True

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
            if now - self._last_waiting_msg > 5.0:
                log.info("게임 대기 중... (LMU 실행 + 공유 메모리 플러그인 활성화 필요)")
                self._last_waiting_msg = now
            return
        if not snap.in_session:
            if now - self._last_waiting_msg > 5.0:
                log.info("세션 대기 중... (게임 연결됨, 세션 없음)")
                self._last_waiting_msg = now
            return

        self.on_snapshot(snap)

        if now - self._last_status >= self.status_interval:
            self._last_status = now
            self.print_status(snap)

    # -- 훅 (이후 마일스톤에서 확장) -----------------------------------------

    def on_snapshot(self, snap: Snapshot) -> None:
        """5Hz마다 호출. v0.2+에서 랩 이벤트 디스패치/분석기 연결."""

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
        log.info(
            "[%s|%s] P%d L%d | 연료 %s | 랩 %s (베스트 %s) | 앞 %s / 뒤 %s | %.0fkm/h",
            ses["track"][:20] or "?", phase,
            me["place"], me["total_laps"] + 1,
            fuel_str,
            fmt_time(me["last_lap"]), fmt_time(me["best_lap"]),
            fmt_gap(gap_ahead), fmt_gap(gap_behind),
            p.get("speed_kmh", 0.0),
        )

    def shutdown(self) -> None:
        self._running = False
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
