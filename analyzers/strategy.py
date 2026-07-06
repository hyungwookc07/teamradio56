"""
LLM 전략 엔진 — "판단이 필요한 순간"에만 LLM 전략 멘트를 트리거한다.

호출 예산이 핵심 제약(레이스 2시간 기준 10~30회)이므로 트리거는 보수적으로:
  - 피트 윈도우 개방 (pit_needed False→True 전이)
  - 같은 클래스 앞차와의 갭 추세 반전
  - 날씨(강수) 유의미 변화
매 랩 돌지만 트리거가 없으면 아무것도 하지 않는다 (침묵 기본값).
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("strategy")

GAP_RATE_MIN = 0.3      # 초/랩 — 이 이상이어야 '추세'로 인정
RAIN_DELTA_MIN = 0.15


class StrategyEngine:
    def __init__(self, cfg):
        self._pit_needed = False
        self._last_rain: Optional[float] = None

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus,
               fuel_status: Optional[dict], tyre_status: Optional[dict]) -> None:
        triggers: list[str] = []

        # 1) 피트 윈도우 개방 전이
        if fuel_status is not None:
            pit_needed = bool(fuel_status.get("pit_needed"))
            if pit_needed and not self._pit_needed:
                triggers.append("피트 윈도우가 방금 열렸다. 타이어 상태와 엮어 언제 들어올지 판단해라.")
            self._pit_needed = pit_needed
            # 서사 이슈 유지
            if pit_needed and fuel_status.get("pit_window_laps") is not None:
                state.set_issue("fuel", f"연료 {fuel_status['fuel_laps']}랩 분량, "
                                        f"늦어도 {fuel_status['pit_window_laps']}랩 안 피트 필요")
            else:
                state.clear_issue("fuel")

        # 2) 갭 추세 반전 (같은 클래스 앞차)
        rev = self._gap_reversal(state)
        if rev:
            triggers.append(rev)

        # 3) 날씨 변화
        rain = snap.session["raining"]
        if self._last_rain is None:
            self._last_rain = rain
        elif abs(rain - self._last_rain) >= RAIN_DELTA_MIN:
            direction = "강해지고" if rain > self._last_rain else "약해지고"
            triggers.append(f"비가 {direction} 있다 (강수 {self._last_rain:.1f}→{rain:.1f}). "
                            "타이어/피트 전략 판단을 말해라.")
            state.set_issue("weather", f"강수 변화 중 ({rain:.1f})")
            self._last_rain = rain
        elif rain < 0.05:
            state.clear_issue("weather")

        # 타이어 이슈 유지
        if tyre_status and tyre_status.get("worst"):
            w = tyre_status["worst"]
            if w["laps_left"] <= 15:
                state.set_issue("tyres", f"{w['wheel']} 타이어 수명 약 {w['laps_left']:.0f}랩")
            else:
                state.clear_issue("tyres")

        if not triggers:
            return
        lap_no = state.laps[-1].lap_number if state.laps else 0
        bus.push(Event(
            type=EventType.LAP_ANALYSIS, priority=Priority.NORMAL,
            data={**(fuel_status or {}), "triggers": triggers},
            dedup_key=f"strategy_{lap_no}",
        ))
        log.info("전략 트리거: %s", "; ".join(t.split(".")[0] for t in triggers))

    def _gap_reversal(self, state: SessionState) -> Optional[str]:
        """최근 갭 변화율의 부호가 이전 구간과 뒤집혔는가."""
        valid = [l for l in state.laps if l.valid and l.gap_ahead >= 0]
        if len(valid) < 5:
            return None
        recent = (valid[-1].gap_ahead - valid[-3].gap_ahead) / 2
        prev = (valid[-3].gap_ahead - valid[-5].gap_ahead) / 2
        if abs(recent) < GAP_RATE_MIN or abs(prev) < GAP_RATE_MIN:
            return None
        if (recent > 0) == (prev > 0):
            return None
        direction = "좁혀지다가 다시 벌어지기" if recent > 0 else "벌어지다가 다시 좁혀지기"
        return f"앞차와의 갭이 {direction} 시작했다. 페이스 전략 판단을 말해라."
