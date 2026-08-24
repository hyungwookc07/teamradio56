"""
리플레이 회귀 (파이썬 기준값) — C#의 tests/TeamRadio56.Replay와 짝.

녹화 JSONL을 분석기 파이프라인(현재: 트래픽)에 먹이고, 이벤트 버스가
"수락한" 이벤트를 JSONL로 출력한다. C# 러너 출력과 이 출력이 같으면
포팅이 파이썬과 같은 판단·타이밍·데이터를 낸다는 뜻이다.
(멘트 문구/랜덤 선택은 비교 대상이 아니다 — 그건 보이스 계층.)

결정성:
  - 버스의 시계를 가상 시계(스냅샷 기록 시각)로 패치 — 쿨다운 판정이
    재생 속도와 무관해진다.
  - 설정은 항상 DEFAULTS (config.yaml을 읽지 않음) — 사용자 설정에
    따라 결과가 달라지지 않게.

사용법: python tools/replay_calls.py --replay race.jsonl[.gz] [--out calls.jsonl]
"""

from __future__ import annotations

import argparse
import copy
import gzip
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import events                                    # noqa: E402
from events import Event, EventBus, EventType, Priority  # noqa: E402
from state import SessionState                   # noqa: E402
from analyzers.traffic import TrafficAnalyzer    # noqa: E402
from analyzers.racecontrol import RaceControlAnalyzer  # noqa: E402
from analyzers.health import HealthAnalyzer      # noqa: E402
from analyzers.rivals import RivalAnalyzer       # noqa: E402
from analyzers.fuel import FuelAnalyzer          # noqa: E402
from analyzers.pace import PaceAnalyzer          # noqa: E402
from analyzers.tyres import TyreAnalyzer         # noqa: E402
from analyzers.strategy import StrategyEngine    # noqa: E402
from analyzers.reporter import StatusReporter    # noqa: E402
from config import load_config                   # noqa: E402
from telemetry import Snapshot                   # noqa: E402


class _VirtualTime:
    """events.time 대체 — monotonic()이 리플레이 기록 시각을 반환."""

    def __init__(self):
        self.t = 0.0

    def monotonic(self) -> float:
        return self.t


def open_replay(path: str):
    if path.endswith(".gz"):
        return gzip.open(path, "rt", encoding="utf-8")
    return open(path, "r", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--replay", required=True)
    parser.add_argument("--out", default=None)
    args = parser.parse_args()

    # 존재하지 않는 경로 → 항상 DEFAULTS 사용 (회귀 결정성)
    cfg = load_config("__defaults_only__")

    vclock = _VirtualTime()
    events.time = vclock          # events.py의 time.monotonic() 호출을 가로챔

    bus = EventBus(cfg["cooldowns"])
    state = SessionState()
    traffic = TrafficAnalyzer(cfg)
    racecontrol = RaceControlAnalyzer(cfg)
    health = HealthAnalyzer(cfg)
    rivals = RivalAnalyzer(cfg)
    fuel = FuelAnalyzer(cfg)
    pace = PaceAnalyzer(cfg)
    tyres = TyreAnalyzer(cfg)
    strategy = StrategyEngine(cfg)
    reporter = StatusReporter(cfg)

    accepted: list[dict] = []
    orig_push = bus.push

    def recording_push(ev):
        ok = orig_push(ev)
        if ok:
            accepted.append({
                "t": round(vclock.t, 2),
                "type": ev.type,
                "prio": int(ev.priority),
                "tone": ev.tone,
                "key": ev.key,
                "message": ev.message,
                # push 시점 상태를 기록 — 분석기가 나중에 dict를 변형해도
                # (pace의 gap_trend 등) 기록이 흔들리지 않게 깊은 복사
                "data": copy.deepcopy(ev.data),
            })
        return ok

    bus.push = recording_push

    ticks = 0
    with open_replay(args.replay) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            snap = Snapshot.from_json(line)
            if not snap.connected:
                continue
            vclock.t = snap.t
            # main.py on_snapshot 순서: 분석기 틱 → 상태 갱신(랩 완료 감지)
            traffic.on_tick(state, snap, bus)
            racecontrol.on_tick(state, snap, bus)
            rivals.on_tick(state, snap, bus)
            health.on_tick(state, snap, bus)
            lap = state.update(snap)
            if lap is not None:
                # main.py on_lap_complete 순서 그대로
                fuel_status = fuel.on_lap(state, snap, bus)
                pace.on_lap(state, snap, bus, lap)
                tyre_status = tyres.on_lap(state, snap, bus)
                if lap.in_pits and state.is_race:
                    bus.push(Event(
                        type=EventType.STINT_BRIEFING, priority=Priority.NORMAL,
                        data={}, dedup_key=f"stint_{lap.lap_number}",
                    ))
                strategy.on_lap(state, snap, bus, fuel_status, tyre_status)
                reporter.on_lap(state, snap, bus, fuel_status, tyre_status)
                racecontrol.on_lap(state, snap, bus)
                rivals.on_lap(state, snap, bus)
                health.on_lap(state, snap, bus)
            ticks += 1

    out = open(args.out, "w", encoding="utf-8") if args.out else sys.stdout
    for rec in accepted:
        out.write(json.dumps(rec, sort_keys=True, ensure_ascii=False,
                             separators=(",", ":")) + "\n")
    if args.out:
        out.close()
        print(f"틱 {ticks}개 처리, 수락 이벤트 {len(accepted)}개 → {args.out}",
              file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
