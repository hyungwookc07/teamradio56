"""
레이스 종료(체커) 판단 — 마지막 랩/남은 랩/내 피니시. C# Logic/RaceEnd.cs 미러.
rF2/LMU: 시간제는 시계 0 이후 전체 1위(클래스 무관)가 라인을 넘을 때 체커, 나머지는 그 뒤
각자 첫 라인 통과에서 피니시. 랩제는 1위가 max_laps를 채울 때. 그래서 랩 다운·느린 클래스는
자기 페이스로 센 것보다 한 랩 더(또는 덜) 돈다. 페이즈 8은 '리더' 피니시다.
"""

from __future__ import annotations

import math
from typing import Optional

LAP_LIMIT_MAX = 100000        # 시간제에선 max_laps에 INT_MAX가 들어온다
LEADER_PACE_MAX_RATIO = 1.2   # 직전 랩이 베스트의 1.2배 초과(피트·FCY)면 베스트로
JUST_CROSSED_S = 5.0          # 랩 카운트는 넘어갔는데 lap_dist가 아직 안 감긴 순간
LATE_MIN_TO_LINE_S = 10.0     # 라인까지 이보다 가까우면 늦은 '마지막 랩'은 피니시와 겹친다
RACING_PHASES = (5, 6, 8)     # 그린/FCY/체커 이후(리더만 끝남)
PHASE_OVER = 8


def lap_pace(v) -> Optional[float]:
    last, best = v["last_lap"], v["best_lap"]
    if last > 0 and (best <= 0 or last <= best * LEADER_PACE_MAX_RATIO):
        return last
    if best > 0:
        return best
    return None


def lap_frac(v, now, track_len, pace) -> float:
    lse = v.get("lap_start_et", 0.0)
    if track_len > 0:
        d = v["lap_dist"]
        if d < 0:
            d += track_len
        f = d / track_len
        if f > 0.5 and lse > 0 and 0 <= now - lse < JUST_CROSSED_S:
            f = 0.0
    elif lse > 0:
        f = (now - lse) / pace
    else:
        f = 0.0
    return min(max(f, 0.0), 1.0)


def flag_out(snap) -> bool:
    if snap.session["game_phase"] == PHASE_OVER:
        return True
    return any(v["finish_status"] == 1 for v in snap.vehicles)


def race_leader(snap):
    best = None
    for v in snap.vehicles:
        if v["place"] <= 0 or v["finish_status"] != 0 or v.get("in_garage", False):
            continue          # DNF/DQ·차고 차는 체커를 받을 수 없다
        if best is None or v["place"] < best["place"] \
                or (v["place"] == best["place"] and v["id"] < best["id"]):
            best = v
    return best


class RaceEndTracker:
    def __init__(self):
        self.lap: Optional[int] = None          # 마지막 observe 때 내 total_laps (None=아직 관측 전)
        self.laps_left: Optional[int] = None    # 마지막 라인 통과 기준 남은 랩(지금 랩 포함), 0=피니시
        self.chequered_et: Optional[float] = None   # 리더 체커 시각 (처음 관측된 값)
        self.finished = False
        self.out = False                        # 내 DNF/DQ
        self.seen_unfinished = False

    @property
    def final_lap(self) -> bool:
        return self.laps_left == 1 and not self.finished

    def observe(self, state, snap, crossed: bool) -> None:
        me = snap.player_scoring()
        if me is None or not state.is_race:
            return
        ses = snap.session
        now = ses["current_et"]
        phase = ses["game_phase"]
        if self.chequered_et is None and flag_out(snap):
            # 체커 차의 lap_start_et가 실제 통과 시각 — 같은 틱에 넘은 차들의 선후를 가른다
            ets = [v.get("lap_start_et", 0.0) for v in snap.vehicles
                   if v["finish_status"] == 1 and v.get("lap_start_et", 0.0) > 0]
            self.chequered_et = min(ets) if ets else now
        self.lap = me["total_laps"]
        fs = me["finish_status"]
        if fs >= 2:
            self.out = True
            self.laps_left = None
        if self.finished or self.out:
            return
        if fs == 0 and phase in RACING_PHASES:
            self.seen_unfinished = True
        if fs == 1:
            self.finished = True
            self.laps_left = 0
            return
        if not crossed or phase not in RACING_PHASES:
            return
        lse = me.get("lap_start_et", 0.0)
        t_cross = lse if (lse > 0 and 0 <= now - lse < JUST_CROSSED_S) else now
        self.laps_left = self._estimate(state, snap, me, t_cross)
        if self.laps_left == 0:
            self.finished = True

    def _estimate(self, state, snap, me, t_cross) -> Optional[int]:
        ses = snap.session
        now, end_et = ses["current_et"], ses["end_et"]
        max_laps, track_len = ses["max_laps"], ses["track_len"]
        timed = end_et > 0
        lap_limited = 0 < max_laps < LAP_LIMIT_MAX
        if not (timed or lap_limited):
            return None
        if self.chequered_et is not None:          # 체커 뒤 통과면 피니시, 같은 틱에 리더보다 먼저면 마지막 랩
            return 0 if t_cross >= self.chequered_et else 1
        leader = race_leader(snap)
        lead_me = leader is None or leader["id"] == me["id"]
        if lead_me:
            if (timed and t_cross >= end_et) or (lap_limited and me["total_laps"] >= max_laps):
                return 0
        elif timed and t_cross >= end_et:
            return 1                                # 시계는 끝났고 리더는 아직 — 다음 통과가 피니시
        pace = state.baseline_lap_time() or lap_pace(me)
        if not pace or pace <= 0:
            return max(max_laps - me["total_laps"], 1) if lap_limited else None
        if lead_me:
            c = []
            if timed:
                c.append(math.ceil((end_et - t_cross) / pace))
            if lap_limited:
                c.append(max_laps - me["total_laps"])
            return max(min(c), 1)
        lpace = lap_pace(leader) or pace
        n_lead = now + (1.0 - lap_frac(leader, now, track_len, lpace)) * lpace
        c = []
        if timed:
            c.append(n_lead if n_lead >= end_et
                     else n_lead + math.ceil((end_et - n_lead) / lpace) * lpace)
        if lap_limited:
            c.append(n_lead + max(max_laps - leader["total_laps"] - 1, 0) * lpace)
        f_lead = min(c)
        laps = max(math.ceil((f_lead - t_cross) / pace), 1)
        if lap_limited:
            laps = min(laps, max(max_laps - me["total_laps"], 1))
        return laps
