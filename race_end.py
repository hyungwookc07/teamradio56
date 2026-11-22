"""
레이스 종료(체커) 판단 — 마지막 랩/남은 랩/내 피니시, 그리고 체커 뒤 무전의 순수 규칙
(체커 콜 등급·쿨다운 랩·레이스 요약). C# Logic/RaceEnd.cs 미러.
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

# 체커 뒤 무전 — 체커 콜(수락 시각 end_t)로부터 쿨다운 랩 콜, 레이스 요약까지의 간격.
# 체커 클립이 4~5초라 쿨다운 콜은 잠깐 숨을 돌린 뒤, 요약은 그 뒤 여유를 두고.
POST_COOLDOWN_S = 7.0
POST_SUMMARY_S = 20.0
POST_EPS = 1e-6
FINISH_HELD_BAND = 1          # 스타트 대비 ±1계단은 '비슷' (gained/lost는 2계단부터)
FINISH_PLACE_MAX = 60         # speech_domain.CLASS_POS_MAX — 말할 수 있는 클래스 순위


def finish_call(place: int, start: int, multiclass: bool) -> tuple[str, int, int]:
    """
    체커 콜 등급 → (풀, 스타트 순위, 올라온 계단). 순위는 실제 클래스 순위 그대로 말한다.
      P1 → race_end_class_win(멀티클래스) | race_end_win,  P2~3 → race_end_podium,
      스타트 순위 모름(중간 합류) → race_end_result, 2계단 이상 상승 → race_end_gained,
      2계단 이상 하락 → race_end_lost(숫자는 말하지 않는다), 그 밖 → race_end_held,
      순위가 범위 밖 → race_end_plain. C# RaceEnd.FinishCall.
    """
    if not 1 <= place <= FINISH_PLACE_MAX:
        return "race_end_plain", 0, 0
    start = start if 1 <= start <= FINISH_PLACE_MAX else 0
    gained = start - place if start else 0
    if place == 1:
        return ("race_end_class_win" if multiclass else "race_end_win"), start, gained
    if place <= 3:
        return "race_end_podium", start, gained
    if not start:
        return "race_end_result", 0, 0
    if gained > FINISH_HELD_BAND:
        return "race_end_gained", start, gained
    if gained < -FINISH_HELD_BAND:
        return "race_end_lost", start, gained
    return "race_end_held", start, gained


def is_multiclass(snap) -> bool:
    return len({v["cls"] for v in snap.vehicles}) > 1


def cooldown_pool(still_racing: int, faster: bool) -> str:
    """쿨다운 랩 콜 풀 — 빠른 클래스가 아직 달리면 그 경고, 아니면 달리는 차가 있는지."""
    if faster:
        return "cooldown_lap_faster"
    return "cooldown_lap_traffic" if still_racing > 0 else "cooldown_lap"


def best_lap_rank(snap, me) -> tuple[int, int]:
    """
    (내 베스트 랩의 클래스 순위, 베스트 랩이 있는 동클래스 차 수(나 포함)).
    순위 0 = 내 베스트 랩이 없다. C# RaceEnd.BestLapRank.
    """
    mine = me["best_lap"]
    same = [v for v in snap.vehicles if v["cls"] == me["cls"]]
    timed = sum(1 for v in same if v["best_lap"] > 0)
    if mine <= 0:
        return 0, timed
    rank = 1 + sum(1 for v in same
                   if v["id"] != me["id"] and 0 < v["best_lap"] < mine)
    return rank, timed


def summary_pool(rank: int, timed: int, multiclass: bool) -> str:
    """레이스 요약 풀 — 클래스(단일 클래스면 레이스) 패스티스트 랩, 2~3번째 페이스, 그 밖은 디브리핑."""
    sfx = "_class" if multiclass else ""
    if rank == 1 and timed >= 2:
        return "race_summary_fastest" + sfx
    if rank in (2, 3):
        return "race_summary_pace" + sfx
    return "race_summary_debrief"


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
        # 콜 래치 — 트래커와 수명을 같이 해야 재시작(ET 되감기) 뒤 새 레이스에서 다시 부른다
        self.final_called_for: Optional[int] = None   # '마지막 랩'이 수락된 시점의 내 total_laps
        self.end_called = False
        # 스타트 클래스 순위 — 그린 전(그리드·포메이션) 마지막 관측값. 그린 뒤에 처음 붙었으면
        # (중간 합류) None이라 체커 콜이 순위 변동을 말하지 않는다
        self.start_class_place: Optional[int] = None
        self.seen_green = False
        # 체커 뒤 무전 — 체커 콜이 수락된 시각(snap.t)과 래치
        self.end_t: Optional[float] = None
        self.cooldown_called = False
        self.summary_called = False
        self.pit_in_called = False

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
        if phase in RACING_PHASES:
            self.seen_green = True
        elif not self.seen_green and phase < 5 and me["place"] > 0:
            self.start_class_place = state.class_place_of(snap, me)
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
