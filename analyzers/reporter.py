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
from messages import msg, fmt_laptime, laps_text, seconds_text
from speech_domain import CLASS_POS_MAX, status_fuel_ok
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("reporter")


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
            text = self.laptime_text(lap.lap_time, is_best)
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
    def laptime_text(lap_time: float, is_best: bool) -> str:
        """랩타임 콜 — C# StatusReporter.LaptimeText."""
        text = msg("last_lap_report", t=fmt_laptime(lap_time))
        if is_best:
            text += msg("best_lap_suffix")
        return text

    @staticmethod
    def _compose_status(state: SessionState, lap,
                        fuel_status: Optional[dict],
                        tyre_status: Optional[dict]) -> Optional[str]:
        fuel_laps = fuel_status.get("fuel_laps") if fuel_status else None
        has_tyre = bool(tyre_status and tyre_status.get("worst"))
        tyre_left = tyre_status["worst"].get("laps_left") if has_tyre else None
        return StatusReporter.status_text(lap.class_place, lap.gap_ahead, lap.gap_behind,
                                          fuel_laps, tyre_left, has_tyre)

    @staticmethod
    def status_text(class_place: int, gap_ahead: float, gap_behind: float,
                    fuel_laps: Optional[float], tyre_left: Optional[float],
                    has_tyre: bool) -> Optional[str]:
        """상황 리포트 한 문장 — 순수 조립 (C# StatusReporter.StatusText).
        P61 이상·연료 100랩 이상은 그 부분만 뺀다 (음성 팩 3단계 도메인)."""
        parts: list[str] = []
        if 0 < class_place <= CLASS_POS_MAX:
            parts.append(msg("status_pos", p=class_place))
        gaps: list[str] = []
        if 0 <= gap_ahead <= 60:
            gaps.append(msg("status_gap_ahead", g=gap_ahead, gs=seconds_text(gap_ahead)))
        if 0 <= gap_behind <= 60:
            gaps.append(msg("status_gap_behind", g=gap_behind, gs=seconds_text(gap_behind)))
        if gaps:
            parts.append(msg("status_gaps", gaps=msg("status_gap_join").join(gaps)))
        if fuel_laps is not None and status_fuel_ok(fuel_laps):
            parts.append(msg("status_fuel", n=laps_text(fuel_laps)))
        if has_tyre:
            if tyre_left is not None and tyre_left <= 20:
                parts.append(msg("status_tyres", n=laps_text(tyre_left)))
            else:
                parts.append(msg("status_tyres_good"))
        if not parts:
            return None
        return " ".join(parts)
