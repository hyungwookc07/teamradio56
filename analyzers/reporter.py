"""
정기 상황 리포터 — HUD를 끄고 달리기 위한 무전 케이던스 (선택 기능).

실제 내구레이스 무전처럼 정보를 정기적으로 불러줘서 화면 HUD를 대체한다:
  - 랩타임 콜: 매 랩 완료 직후 ("이번 랩 2분 1.8초. 베스트야.")
  - 상황 리포트: N랩마다 순위/앞뒤 갭/연료/타이어 종합 한 문장
    ("P4. 앞 3.2초, 뒤 5.1초. 연료 12랩. 타이어 아직 좋아.")

기본값은 꺼짐 — '말할 필요 없으면 침묵' 철학의 예외라 명시적으로 켠다
(config의 reports 섹션). 켜도 긴급 콜이 대기 중이면 뒤로 밀리고,
TTL이 지나면 조용히 버려진다 (오래된 랩타임은 가치가 없다).
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("reporter")


def _fmt_laptime(sec: float) -> str:
    m, s = divmod(sec, 60.0)
    return f"{int(m)} {s:04.1f}" if m >= 1 else f"{s:.1f}"


class StatusReporter:
    def __init__(self, cfg):
        self.laptime_on = bool(cfg.get("reports.laptime_every_lap", False))
        self.status_laps = int(cfg.get("reports.status_every_laps", 0) or 0)
        self.reset()

    def reset(self) -> None:
        self._last_status_lap = 0

    @property
    def enabled(self) -> bool:
        return self.laptime_on or self.status_laps > 0

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus,
               fuel_status: Optional[dict], tyre_status: Optional[dict]) -> None:
        if not self.enabled or not state.laps:
            return
        lap = state.laps[-1]

        if self.laptime_on and lap.valid and lap.lap_time > 0:
            valid_times = [l.lap_time for l in state.laps if l.valid]
            is_best = len(valid_times) >= 2 and lap.lap_time <= min(valid_times)
            text = f"Last lap {_fmt_laptime(lap.lap_time)}."
            if is_best:
                text += " Best lap."
            bus.push(Event(
                type=EventType.LAP_TIME_REPORT, priority=Priority.NORMAL,
                message=text, dedup_key=f"laptime_{lap.lap_number}",
                ttl=15.0, tone="casual",     # 다음 랩 중반이면 이미 낡은 정보
            ))

        if self.status_laps > 0 \
                and lap.lap_number - self._last_status_lap >= self.status_laps:
            message = self._compose_status(state, lap, fuel_status, tyre_status)
            if message and bus.push(Event(
                type=EventType.STATUS_REPORT, priority=Priority.NORMAL,
                message=message, dedup_key=f"status_{lap.lap_number}",
                ttl=40.0, tone="casual",
            )):
                self._last_status_lap = lap.lap_number

    @staticmethod
    def _compose_status(state: SessionState, lap,
                        fuel_status: Optional[dict],
                        tyre_status: Optional[dict]) -> Optional[str]:
        parts: list[str] = []
        if lap.class_place > 0:
            parts.append(f"P{lap.class_place}.")
        gaps: list[str] = []
        if 0 <= lap.gap_ahead <= 60:
            gaps.append(f"ahead {lap.gap_ahead:.1f}")
        if 0 <= lap.gap_behind <= 60:
            gaps.append(f"behind {lap.gap_behind:.1f}")
        if gaps:
            parts.append("Gap " + ", ".join(gaps) + ".")
        if fuel_status and fuel_status.get("fuel_laps") is not None:
            parts.append(f"Fuel {fuel_status['fuel_laps']:.0f} laps.")
        if tyre_status and tyre_status.get("worst"):
            left = tyre_status["worst"].get("laps_left")
            if left is not None and left <= 20:
                parts.append(f"Tyres {left:.0f} laps.")
            else:
                parts.append("Tyres good.")
        if not parts:
            return None
        return " ".join(parts)
