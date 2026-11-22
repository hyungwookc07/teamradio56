"""
라이벌 인텔 분석기 — 동클래스 경쟁자 관찰.

  - 경쟁자 피트 진입 감지 (5Hz): 언더컷/오버컷 판단 재료. 클래스 순위 ±2 이내의
    차만 콜한다 (멀리 있는 차 피트는 소음).
    피트 플래그는 레이스 틱마다(페이즈 무관) 추적하고 콜만 거른다:
      · 그린/FCY(5·6)에서만 — 그리드·포메이션·롤링 스타트 대기(0~4)는 무음
      · 처음 보는 차(또는 리셋·틱 공백 뒤)는 기준값만 잡는다 — 이미 피트에 있던 차가
        "방금 피트 인"이 되지 않게
      · 앞/뒤는 진입 직전 틱의 순위로 — 진입 틱에 순위가 흔들려도 앞차가 뒤차가 안 되게
      · 순위가 비었거나(0) 같으면 침묵, 차고로 순간이동한 차도 침묵
      · "언더컷"(뒤차)은 그린, 상대가 1랩 이상 돈 뒤, 트랙 위에서 실제로 1/4랩 안쪽
        뒤에 있을 때만 — FCY 피트, 첫 랩 피트, 랩 다운 차는 언더컷이 아니다.
        음성 팩에는 "뒤차 + 갭 안내" 문장이 없어 꼬리를 바꾸지 않고 콜을 뺀다.
  - 페이스 비교 (랩 완료 시): 클래스 바로 앞/뒤 차와 최근 랩 평균 비교 →
    추격 가능성/방어 필요성 인텔.
"""

from __future__ import annotations

import logging
from collections import deque
from typing import Optional

from events import Event, EventBus, EventType, Priority
from speech_domain import rival_diff_ok, rival_pit_ok
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("rivals")

PIT_RELEVANT_RANGE = 2       # 클래스 순위 차이가 이 이내인 경쟁자만 피트 콜
CATCHABLE_LAPS = 15.0        # 이 랩 수 안에 잡을 수 있어야 추격 인텔 발화

# rF2 mGamePhase — 0 세션 전, 1 정찰, 2 그리드 워크, 3 포메이션, 4 카운트다운,
# 5 그린, 6 FCY/SC, 7 중단, 8 종료, 9 일시정지
PHASE_GREEN = 5
PIT_CALL_PHASES = (5, 6)     # 경쟁자 피트 콜은 그린/FCY에서만
UNDERCUT_MAX_LAPS = 0.25     # 언더컷 위협 = 트랙 위에서 이 랩 비율 안쪽 뒤에 있는 차
RESEED_GAP_S = 2.0           # 틱 공백(모니터·일시정지·재시작)이 이보다 길면 기준값 다시 잡기


def pit_data(their_cp: int, my_cp: int, driver: str = "") -> Optional[dict]:
    """경쟁자 피트 콜 데이터 — 클래스 순위 ±2 밖이거나 무의미한 순위면 None.
    순위가 없거나(0 이하) 같으면 앞/뒤를 말할 수 없어 None.
    퍼즈가 같은 함수를 부른다."""
    if their_cp <= 0 or my_cp <= 0 or their_cp == my_cp:
        return None
    if abs(their_cp - my_cp) > PIT_RELEVANT_RANGE or not rival_pit_ok(their_cp):
        return None
    return {
        "driver": driver, "their_class_place": their_cp,
        "rel": "앞" if their_cp < my_cp else "뒤",
        "undercut_risk": their_cp > my_cp,      # 뒤차가 먼저 피트 → 언더컷 위협
    }


def pace_data(diff: float, gap: float, ahead: bool, driver: str = "") -> Optional[dict]:
    """페이스 비교 콜 데이터. diff = 상대 평균 - 우리 평균 (+면 우리가 빠름).
    15랩 안에 잡거나 잡힐 때만, 랩당 5초 넘는 차이(피트/스핀 랩이 섞인 평균)는 None.
    퍼즈가 같은 함수를 부른다."""
    if ahead and diff > 0:                     # 앞차가 더 느림 → 추격 가능
        laps_to_catch = gap / diff
        if laps_to_catch > CATCHABLE_LAPS:
            return None
        data = {"mode": "catch", "driver": driver, "diff": round(diff, 2),
                "laps": max(int(laps_to_catch) + 1, 1), "gap": round(gap, 1)}
    elif not ahead and diff < 0:               # 뒤차가 더 빠름 → 방어 준비
        laps_to_caught = gap / -diff
        if laps_to_caught > CATCHABLE_LAPS:
            return None
        data = {"mode": "defend", "driver": driver, "diff": round(-diff, 2),
                "laps": max(int(laps_to_caught) + 1, 1), "gap": round(gap, 1)}
    else:
        return None
    if not rival_diff_ok(data["diff"]):
        return None
    return data


def _progress(v: dict, track_len: float) -> float:
    """트랙 위 누적 진행 (랩) — 완료 랩 + 이번 랩 비율."""
    return v["total_laps"] + (v["lap_dist"] / track_len if track_len > 0 else 0.0)


class RivalAnalyzer:
    def __init__(self, cfg):
        self.pace_diff_min = cfg.get("thresholds.rival_pace_diff", 0.3)
        self._in_pits: dict[int, bool] = {}
        self._prev: dict[int, tuple] = {}            # cid → 직전 틱 (클래스 순위|0, 진행)
        self._prev_me: Optional[tuple] = None        # 내 직전 틱 (클래스 순위|0, 진행)
        self._last_t: Optional[float] = None
        self._laps_seen: dict[int, deque] = {}      # cid → 최근 랩타임 3개

    def reset(self) -> None:
        self._in_pits.clear()
        self._prev.clear()
        self._prev_me = None
        self._last_t = None
        self._laps_seen.clear()

    # -- 5Hz: 경쟁자 피트 진입 -------------------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        if not state.is_race:
            return
        me = snap.player_scoring()
        if me is None:
            return
        ses = snap.session
        phase = ses["game_phase"]
        tl = ses["track_len"]
        # 틱이 끊겼으면(모니터·일시정지·재시작) 그사이 바뀐 플래그를 진입으로 보지 않는다
        if self._last_t is not None and not (0.0 <= snap.t - self._last_t <= RESEED_GAP_S):
            self._in_pits.clear()
            self._prev.clear()
            self._prev_me = None
        self._last_t = snap.t

        my_now = (state.class_place_of(snap, me) if me["place"] > 0 else 0,
                  _progress(me, tl))
        my_prev, self._prev_me = self._prev_me, my_now

        for v in snap.vehicles:
            if v["is_player"] or v["cls"] != me["cls"] or v["finish_status"] != 0:
                continue
            cid = v["id"]
            was = self._in_pits.get(cid)            # None = 처음 봄 → 기준값만
            now_in = bool(v["in_pits"])
            self._in_pits[cid] = now_in
            prev = self._prev.get(cid)
            cur = (state.class_place_of(snap, v) if v["place"] > 0 else 0,
                   _progress(v, tl))
            self._prev[cid] = cur
            if was is None or was or not now_in:
                continue    # 피트 진입 순간만
            why = self._pit_veto(phase, v, prev, cur, my_prev, my_now)
            data = None
            if why is None:
                data = pit_data(prev[0], my_prev[0], v["driver"])
                if data is None:
                    why = "range"
                elif data["undercut_risk"] and not (
                        phase == PHASE_GREEN and prev[1] >= 1.0
                        and 0.0 < my_prev[1] - prev[1] < UNDERCUT_MAX_LAPS):
                    data, why = None, "not-undercut"
            log.info("라이벌 피트 진입 %s#%d %s · 페이즈 %d · 직전 나 C%d 상대 C%d · "
                     "지금 나 P%d/C%d 상대 P%d/C%d · 랩 %d %.0fm · 차고 %s · 피트 상태 %d",
                     v["driver"], cid, "콜" if data else f"침묵({why})", phase,
                     my_prev[0] if my_prev else -1, prev[0] if prev else -1,
                     me["place"], my_now[0], v["place"], cur[0],
                     v["total_laps"], v["lap_dist"], v["in_garage"], v["pit_state"])
            if data is None:
                continue
            bus.push(Event(
                type=EventType.RIVAL_PIT, priority=Priority.NORMAL,
                data=data,
                dedup_key=f"rpit_{cid}_{v['num_pitstops']}", ttl=25.0,
            ))
            state.add_narrative(
                f"(이벤트) 클래스 {data['rel']} P{data['their_class_place']} {v['driver']} 피트 인")

    @staticmethod
    def _pit_veto(phase: int, v: dict, prev, cur, my_prev, my_now) -> Optional[str]:
        """피트 진입 콜을 막는 사유 (없으면 None). 앞/뒤는 진입 직전 틱 순위로 정한다."""
        if phase not in PIT_CALL_PHASES:
            return "phase"
        if v["in_garage"]:
            return "garage"
        if prev is None or my_prev is None:
            return "no-prev"
        if prev[0] <= 0 or my_prev[0] <= 0 or cur[0] <= 0 or my_now[0] <= 0:
            return "place"
        return None

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
            data = pace_data(diff, gap, ahead, v["driver"])
            if data is None:
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
