"""
연료 분석기 — 랩 완료 시 호출.

최근 3랩 평균 소모량으로 남은 랩 수를 계산하고, 레이스 잔여 랩과 비교해
피트 윈도우를 추정한다. 경고는 임계값 + 쿨다운으로 억제된다.
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from messages import msg
from speech_domain import fuel_save_ok
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("fuel")


def save_message(fuel_l: float, burn: float, left: int,
                 save_delta: float) -> Optional[str]:
    """연료 세이브 코칭 문장 — 목표 미달이면 None, 목표/세이브 값이 무의미하면
    (랩당 0.5~15리터 밖, 3리터 넘는 세이브) 역시 None. C# FuelAnalyzer.SaveMessage."""
    target = fuel_l / left
    delta = burn - target
    if delta < save_delta or not fuel_save_ok(target, delta):
        return None
    return msg("fuel_save", target=target, delta=delta)


class FuelAnalyzer:
    def __init__(self, cfg):
        self.warn_laps = cfg.get("thresholds.fuel_warn_laps", 3.0)
        self.critical_laps = cfg.get("thresholds.fuel_critical_laps", 1.5)
        self.save_delta = cfg.get("thresholds.fuel_save_delta", 0.1)

    def status(self, state: SessionState, snap: Snapshot) -> Optional[dict]:
        """가공된 연료 상태 요약. 분석 불가(데이터 부족)면 None."""
        fuel_now = snap.player.get("fuel")
        if fuel_now is None:
            return None

        burns = [l.fuel_used for l in state.recent_laps(3) if l.fuel_used >= 0.05]
        if not burns:
            return None
        avg_burn = sum(burns) / len(burns)
        if avg_burn <= 0.01:
            return None
        fuel_laps = fuel_now / avg_burn

        race_laps_left = self._race_laps_left(state)
        pit_needed = race_laps_left is not None and fuel_laps < race_laps_left

        return {
            "fuel_l": round(fuel_now, 1),
            "burn_per_lap": round(avg_burn, 2),
            "fuel_laps": round(fuel_laps, 1),
            "race_laps_left": race_laps_left,
            "pit_needed": pit_needed,
            # 안전 마진 1랩을 빼고 이 랩 안에는 들어와야 함
            "pit_window_laps": max(int(fuel_laps) - 1, 0) if pit_needed else None,
        }

    @staticmethod
    def _race_laps_left(state: SessionState) -> Optional[int]:
        """남은 레이스 랩(지금 랩 포함, 0=체커 받음) — race_end가 이번 라인 통과에서 계산한 값."""
        return state.race_end.laps_left if state.is_race else None

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> Optional[dict]:
        st = self.status(state, snap)
        if st is None:
            return None
        if st["race_laps_left"] == 0:
            return st                    # 체커 받은 뒤 연료 콜 무의미

        if st["fuel_laps"] <= self.critical_laps:
            bus.push(Event(
                type=EventType.FUEL_CRITICAL,
                priority=Priority.CRITICAL,
                data=st,
            ))
        elif st["fuel_laps"] <= self.warn_laps:
            bus.push(Event(
                type=EventType.FUEL_WARNING,
                priority=Priority.HIGH,
                data=st,
            ))
        # 피트 윈도우 마지막 랩 도달 → 박스 콜
        if st["pit_needed"] and st["pit_window_laps"] is not None \
                and st["pit_window_laps"] <= 1:
            bus.push(Event(
                type=EventType.PIT_CALL,
                priority=Priority.CRITICAL,
                data=st,
            ))
        self._save_coaching(st, bus)
        return st

    def _save_coaching(self, st: dict, bus: EventBus) -> None:
        """
        연료 세이브 코칭: 노피트로 끝까지 갈 수 있는 경계 상황
        (연료 랩 < 잔여 랩 ≤ 연료 랩+2)에서 목표 소모량 대비 오버 시.
        """
        left = st["race_laps_left"]
        if left is None or left <= 0:
            return
        if not (st["fuel_laps"] < left <= st["fuel_laps"] + 2.0):
            return
        message = save_message(st["fuel_l"], st["burn_per_lap"], left, self.save_delta)
        if message is None:
            return
        target = st["fuel_l"] / left
        delta = st["burn_per_lap"] - target
        bus.push(Event(
            type=EventType.FUEL_SAVE, priority=Priority.NORMAL,
            data={"target": round(target, 2), "delta": round(delta, 2),
                  "laps_left": left},
            message=message,
        ))
