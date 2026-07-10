"""
라이벌 인텔 분석기 — 동클래스 경쟁자 관찰.

  - 경쟁자 피트 진입 감지 (5Hz): 언더컷/오버컷 판단 재료. 클래스 순위 ±2 이내의
    차만 콜한다 (멀리 있는 차 피트는 소음).
  - 페이스 비교 (랩 완료 시): 클래스 바로 앞/뒤 차와 최근 랩 평균 비교 →
    추격 가능성/방어 필요성 인텔.
"""

from __future__ import annotations

import logging
from collections import deque
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("rivals")

PIT_RELEVANT_RANGE = 2       # 클래스 순위 차이가 이 이내인 경쟁자만 피트 콜
CATCHABLE_LAPS = 15.0        # 이 랩 수 안에 잡을 수 있어야 추격 인텔 발화


class RivalAnalyzer:
    def __init__(self, cfg):
        self.pace_diff_min = cfg.get("thresholds.rival_pace_diff", 0.3)
        self._in_pits: dict[int, bool] = {}
        self._laps_seen: dict[int, deque] = {}      # cid → 최근 랩타임 3개

    # -- 5Hz: 경쟁자 피트 진입 -------------------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        if not state.is_race:
            return
        me = snap.player_scoring()
        if me is None:
            return
        my_cp = state.class_place_of(snap, me)

        for v in snap.vehicles:
            if v["is_player"] or v["cls"] != me["cls"] or v["finish_status"] != 0:
                continue
            was = self._in_pits.get(v["id"], False)
            now_in = bool(v["in_pits"])
            self._in_pits[v["id"]] = now_in
            if not (now_in and not was):
                continue    # 피트 진입 순간만
            their_cp = state.class_place_of(snap, v)
            if abs(their_cp - my_cp) > PIT_RELEVANT_RANGE:
                continue
            rel = "앞" if their_cp < my_cp else "뒤"
            undercut_risk = their_cp > my_cp        # 뒤차가 먼저 피트 → 언더컷 위협
            bus.push(Event(
                type=EventType.RIVAL_PIT, priority=Priority.NORMAL,
                data={
                    "driver": v["driver"], "their_class_place": their_cp,
                    "rel": rel, "undercut_risk": undercut_risk,
                },
                dedup_key=f"rpit_{v['id']}_{v['num_pitstops']}", ttl=25.0,
            ))
            state.add_narrative(
                f"(이벤트) 클래스 {rel} P{their_cp} {v['driver']} 피트 인")

    # -- 랩 완료: 페이스 비교 인텔 ----------------------------------------------

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        if not state.is_race:
            return
        me = snap.player_scoring()
        if me is None:
            return
        self._record_rival_laps(snap, me)

        my_avg = self._avg(state.laps and [l.lap_time for l in state.laps if l.valid][-3:])
        if my_avg is None or len(state.laps) < 5:
            return
        my_cp = state.class_place_of(snap, me)

        for v in snap.vehicles:
            if v["is_player"] or v["cls"] != me["cls"] or v["in_pits"] \
                    or v["finish_status"] != 0:
                continue
            their_cp = state.class_place_of(snap, v)
            if abs(their_cp - my_cp) != 1:
                continue    # 클래스 바로 앞/뒤만
            their_avg = self._avg(list(self._laps_seen.get(v["id"], [])))
            if their_avg is None:
                continue
            diff = their_avg - my_avg    # +면 우리가 빠름
            if abs(diff) < self.pace_diff_min:
                continue

            ahead = their_cp < my_cp
            gap = state.laps[-1].gap_ahead if ahead else state.laps[-1].gap_behind
            if gap is None or gap < 0:
                continue
            if ahead and diff > 0:                     # 앞차가 더 느림 → 추격 가능
                laps_to_catch = gap / diff
                if laps_to_catch > CATCHABLE_LAPS:
                    continue
                data = {"mode": "catch", "driver": v["driver"], "diff": round(diff, 2),
                        "laps": max(int(laps_to_catch) + 1, 1), "gap": round(gap, 1)}
            elif not ahead and diff < 0:               # 뒤차가 더 빠름 → 방어 준비
                laps_to_caught = gap / -diff
                if laps_to_caught > CATCHABLE_LAPS:
                    continue
                data = {"mode": "defend", "driver": v["driver"], "diff": round(-diff, 2),
                        "laps": max(int(laps_to_caught) + 1, 1), "gap": round(gap, 1)}
            else:
                continue
            bus.push(Event(
                type=EventType.RIVAL_PACE, priority=Priority.NORMAL,
                data=data, dedup_key=f"rpace_{v['id']}_{data['mode']}",
            ))
            break    # 한 랩에 한 건만

    def _record_rival_laps(self, snap: Snapshot, me: dict) -> None:
        for v in snap.vehicles:
            if v["is_player"] or v["cls"] != me["cls"]:
                continue
            t = v["last_lap"]
            if t <= 0:
                continue
            dq = self._laps_seen.setdefault(v["id"], deque(maxlen=3))
            if not dq or abs(dq[-1] - t) > 1e-3:
                dq.append(t)

    @staticmethod
    def _avg(times) -> Optional[float]:
        times = [t for t in (times or []) if t and t > 0]
        if len(times) < 2:
            return None
        return sum(times) / len(times)
