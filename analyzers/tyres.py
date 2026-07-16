"""
타이어 분석기 — 랩 완료 시 호출.

현재값이 아니라 랩 히스토리의 추세를 본다:
  - 좌/우, 전/후 캐리커스 온도 불균형 (셋업/데미지/스타일 문제 신호)
  - 최근 랩 마모율 → 예상 수명 (마모 한계까지 남은 랩 수)
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("tyres")

WHEEL_NAMES = ["왼쪽 앞", "오른쪽 앞", "왼쪽 뒤", "오른쪽 뒤"]
WEAR_CLIFF = 0.25          # 이 잔량 아래로는 그립 절벽으로 간주
TREND_WINDOW = 3           # 마모율 계산에 쓰는 랩 수


class TyreAnalyzer:
    def __init__(self, cfg):
        self.temp_imbalance = cfg.get("thresholds.tyre_temp_imbalance", 12.0)
        self.wear_warn = cfg.get("thresholds.tyre_wear_warn", 0.35)

    def status(self, state: SessionState) -> Optional[dict]:
        """가공된 타이어 상태 요약. 데이터 부족이면 None."""
        valid = [l for l in state.laps if l.valid and len(l.tyre_wear) == 4]
        if not valid:
            return None
        last = valid[-1]

        result: dict = {
            "wear": last.tyre_wear,           # 잔량 비율 FL FR RL RR
            "temps": last.tyre_temps,         # 캐리커스 C
        }

        # 온도 불균형 (좌우 / 전후)
        t = last.tyre_temps
        if len(t) == 4 and all(v > 0 for v in t):
            result["imbalance"] = {
                "front_lr": round(t[1] - t[0], 1),   # +면 우측 앞이 더 뜨거움
                "rear_lr": round(t[3] - t[2], 1),
                "front_rear": round((t[0] + t[1]) / 2 - (t[2] + t[3]) / 2, 1),
            }

        # 마모 추세 → 예상 수명 (최악 휠 기준)
        if len(valid) > TREND_WINDOW:
            base = valid[-1 - TREND_WINDOW]
            worst = None
            for i in range(4):
                rate = (base.tyre_wear[i] - last.tyre_wear[i]) / TREND_WINDOW
                if rate <= 1e-4:
                    continue
                laps_left = max((last.tyre_wear[i] - WEAR_CLIFF) / rate, 0.0)
                if worst is None or laps_left < worst["laps_left"]:
                    worst = {
                        "wheel": WHEEL_NAMES[i],
                        "wheel_idx": i,
                        "wear_rate": round(rate, 4),
                        "remaining": round(last.tyre_wear[i], 3),
                        "laps_left": round(laps_left, 1),
                    }
            if worst is not None:
                result["worst"] = worst
        return result

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> Optional[dict]:
        st = self.status(state)
        if st is None:
            return None

        # 펑크/탈락은 즉시 긴급 콜
        wheels = snap.player.get("wheels") or []
        for i, w in enumerate(wheels[:4]):
            if w.get("flat") or w.get("detached"):
                bus.push(Event(
                    type=EventType.DAMAGE, priority=Priority.CRITICAL,
                    data={"wheel": WHEEL_NAMES[i]},
                    dedup_key=f"flat_{i}",
                    message=f"{WHEEL_NAMES[i]} 펑크! 바로 박스. 무리하지 마.",
                ))

        # 온도 불균형 경고
        imb = st.get("imbalance")
        if imb is not None:
            axis, delta = max(
                (("front_lr", imb["front_lr"]), ("rear_lr", imb["rear_lr"])),
                key=lambda kv: abs(kv[1]))
            if abs(delta) >= self.temp_imbalance:
                hot = {"front_lr": WHEEL_NAMES[1] if delta > 0 else WHEEL_NAMES[0],
                       "rear_lr": WHEEL_NAMES[3] if delta > 0 else WHEEL_NAMES[2]}[axis]
                bus.push(Event(
                    type=EventType.TYRE_WARNING, priority=Priority.NORMAL,
                    data={"kind": "temp_imbalance", "hot_wheel": hot,
                          "delta": round(abs(delta), 1), **st},
                ))

        # 예상 수명 경고
        worst = st.get("worst")
        if worst is not None and worst["remaining"] <= self.wear_warn:
            bus.push(Event(
                type=EventType.TYRE_WARNING, priority=Priority.HIGH,
                data={"kind": "wear", **worst, **st},
            ))
        return st
