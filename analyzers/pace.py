"""
페이스 분석기 — 랩 완료 시 호출.

랩타임이 '평소'(최근 유효 랩 중앙값) 대비 유의미하게 다를 때,
같은 클래스 앞/뒤 갭의 변화율이 클 때만 이벤트를 낸다. 그 외엔 침묵.
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import LapRecord, SessionState
from telemetry import Snapshot

log = logging.getLogger("pace")

MIN_LAPS_FOR_BASELINE = 4

# 배틀 갭 리포트 — 동클래스 앞/뒤차가 이 이내면 '배틀 문맥'으로 보고,
# 큰 추세(gap_change_sec_per_lap)가 아니어도 주기적으로 갭 상황을 알려준다.
# 실제 엔지니어처럼 "앞차 2초, 랩당 0.2씩 좁혀지는 중" 식의 리포트.
BATTLE_REPORT_GAP_SEC = 10.0
BATTLE_MIN_CHANGE_SEC = 0.3    # 지난 리포트 대비 이만큼은 변해야 다시 말함


class PaceAnalyzer:
    def __init__(self, cfg):
        self.pace_delta = cfg.get("thresholds.pace_delta_sec", 0.7)
        self.gap_rate = cfg.get("thresholds.gap_change_sec_per_lap", 0.4)
        self.reset()

    def reset(self) -> None:
        self._last_report: dict[str, float] = {}   # who → 마지막 리포트한 갭

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus,
               lap: LapRecord) -> Optional[dict]:
        if not lap.valid:
            return None
        valid = [l for l in state.laps if l.valid]
        if len(valid) < MIN_LAPS_FOR_BASELINE:
            return None

        # 방금 랩을 제외한 최근 유효 랩들의 중앙값 = 평소 페이스
        prior = sorted(l.lap_time for l in valid[:-1][-5:])
        baseline = prior[len(prior) // 2]
        delta = lap.lap_time - baseline

        result = {
            "lap": lap.lap_number,
            "lap_time": lap.lap_time,
            "baseline": round(baseline, 3),
            "delta": round(delta, 3),
            "place": lap.place,
        }

        if abs(delta) >= self.pace_delta:
            result["direction"] = "slower" if delta > 0 else "faster"
            bus.push(Event(
                type=EventType.PACE_COMMENT,
                priority=Priority.NORMAL,
                data=result,
                dedup_key=f"pace_{lap.lap_number}",
            ))

        gap_ev = self._gap_trend(valid) or self._battle_report(valid)
        if gap_ev is not None:
            result["gap_trend"] = gap_ev
            self._last_report[gap_ev["who"]] = gap_ev["gap"]
            bus.push(Event(
                type=EventType.GAP_COMMENT,
                priority=Priority.NORMAL,
                data=gap_ev,
                dedup_key=f"gap_{lap.lap_number}",
            ))
        return result

    def _gap_trend(self, valid: list[LapRecord]) -> Optional[dict]:
        """최근 3랩 동안 앞/뒤 갭 변화율 (초/랩). 큰 쪽 하나만 보고한다."""
        if len(valid) < 3:
            return None
        window = valid[-3:]

        candidates = []
        for attr, who in (("gap_ahead", "ahead"), ("gap_behind", "behind")):
            gaps = [getattr(l, attr) for l in window]
            if any(g < 0 for g in gaps):        # 갭 정보 없음 (선두거나 최후미)
                continue
            rate = (gaps[-1] - gaps[0]) / (len(gaps) - 1)
            if abs(rate) >= self.gap_rate:
                candidates.append({
                    "who": who,                  # ahead=앞차, behind=뒷차
                    "gap": round(gaps[-1], 1),
                    "rate": round(rate, 2),      # +면 벌어짐, -면 좁혀짐
                })
        if not candidates:
            return None
        return max(candidates, key=lambda c: abs(c["rate"]))

    def _battle_report(self, valid: list[LapRecord]) -> Optional[dict]:
        """
        배틀 문맥 갭 리포트 — 동클래스 앞/뒤차가 10초 이내면 큰 추세가 아니어도
        갭 상황을 알린다. 처음 사정권에 들어왔을 때 한 번, 이후엔 지난 리포트
        대비 갭이 유의미하게 변했을 때만 (쿨다운은 이벤트 버스가 별도 적용).
        """
        if len(valid) < 2:
            return None
        prev, cur = valid[-2], valid[-1]

        candidates = []
        for attr, who in (("gap_ahead", "ahead"), ("gap_behind", "behind")):
            g = getattr(cur, attr)
            p = getattr(prev, attr)
            if not (0 <= g <= BATTLE_REPORT_GAP_SEC) or p < 0:
                continue
            last = self._last_report.get(who)
            if last is not None and abs(g - last) < BATTLE_MIN_CHANGE_SEC:
                continue    # 지난 리포트와 비슷하면 침묵
            candidates.append({
                "who": who,
                "gap": round(g, 1),
                "rate": round(g - p, 2),   # 최근 1랩 변화 (+벌어짐 / -좁혀짐)
            })
        if not candidates:
            # 배틀에서 벗어난 쪽은 기억을 지워 다음 배틀 진입 때 다시 리포트
            for attr, who in (("gap_ahead", "ahead"), ("gap_behind", "behind")):
                if not (0 <= getattr(cur, attr) <= BATTLE_REPORT_GAP_SEC):
                    self._last_report.pop(who, None)
            return None
        return min(candidates, key=lambda c: c["gap"])   # 더 가까운 쪽 하나만
