"""
차량 컨디션 분석기 — 엔진(수온/유온/과열)과 브레이크 온도 (랩 완료 시 호출).

임계값 초과 시에만 발화. 쿨다운(engine_warning)으로 반복 억제.
"""

from __future__ import annotations

import logging

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("health")


class HealthAnalyzer:
    def __init__(self, cfg):
        self.water_warn = cfg.get("thresholds.water_temp_warn", 105.0)
        self.oil_warn = cfg.get("thresholds.oil_temp_warn", 115.0)
        self.brake_warn = cfg.get("thresholds.brake_temp_warn", 700.0)

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p:
            return

        water = p.get("water_temp", 0.0)
        oil = p.get("oil_temp", 0.0)
        if p.get("overheating") or water >= self.water_warn or oil >= self.oil_warn:
            what = "수온" if water >= self.water_warn else "유온" if oil >= self.oil_warn else "엔진"
            bus.push(Event(
                type=EventType.ENGINE_WARNING, priority=Priority.HIGH,
                message=f"{what}이 올라가고 있어. 앞차 슬립스트림에서 나와서 공기 좀 먹이자.",
                data={"water": water, "oil": oil}, ttl=30.0,
            ))
            state.set_issue("engine", f"엔진 온도 상승 (수온 {water:.0f}, 유온 {oil:.0f})")
        else:
            state.clear_issue("engine")

        wheels = p.get("wheels") or []
        if len(wheels) == 4:
            avg_brake = sum(w.get("brake_temp", 0.0) for w in wheels) / 4
            if avg_brake >= self.brake_warn:
                bus.push(Event(
                    type=EventType.BRAKE_WARNING, priority=Priority.NORMAL,
                    message=f"브레이크가 평균 {avg_brake:.0f}도야. "
                            "브레이킹 한 템포 일찍 시작해서 식히자.",
                    data={"avg_brake": round(avg_brake)}, ttl=30.0,
                ))
