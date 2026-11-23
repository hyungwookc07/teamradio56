"""
레이스 컨트롤 분석기 — 코스 상태/레이스 진행 (5Hz 매 틱 + 랩 완료 시 호출).

담당:
  - FCY(풀코스옐로)/세이프티카 진입·해제, FCY 중 피트 오픈 (내구레이스 전략 핵심)
  - 섹터 로컬 옐로
  - 레이스 스타트(그린)/체커, 남은 시간 마일스톤, 마지막 랩
  - 체커 뒤 무전: 쿨다운 랩(아직 달리는 차 경고) → 레이스 요약 한 번 → 피트인
  - 피트레인 리미터 미작동 경고
  - 클래스 순위 변동 콜
내 레이스가 끝나면(state.race_over) 블루 플래그·페널티·순위 변동은 끄고, 섹터 옐로와
피트 리미터(안전)만 남긴다.
"""

from __future__ import annotations

import logging
from typing import Optional

from vehicle_class import class_pace, class_relation
from events import Event, EventBus, EventType, Priority
from messages import msg, penalty_text
from race_end import (LATE_MIN_TO_LINE_S, POST_COOLDOWN_S, POST_EPS, POST_SUMMARY_S,
                      RACING_PHASES, best_lap_rank, cooldown_pool, finish_call, flag_out,
                      is_multiclass, lap_frac, lap_pace, race_leader, summary_pool)
from speech_domain import CLASS_POS_MAX
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("racecontrol")

# rF2 게임 페이즈
PHASE_GREEN = 5
PHASE_FCY = 6
PHASE_OVER = 8
# rF2 옐로 상태 (mYellowFlagState — 풀코스 전용: 1 대기 … 6 리스타트 준비, 7 레이스 중단)
Y_PIT_CLOSED = 2
Y_PIT_LEAD_LAP = 3
Y_PIT_OPEN = 4
Y_FCY_MIN = 1
Y_FCY_MAX = 7
# 섹터 플래그 (ScoringInfo.mSectorFlag[i] = 섹터 i+1) — 원시값 1만 로컬 옐로
SECTOR_FLAG_YELLOW = 1
SECTOR_YELLOW_REARM_S = 120.0      # 같은 섹터 재콜 최소 간격 (푸시가 버스에 거절돼도 기록)


def fcy_active(phase: int, yellow: int) -> bool:
    """풀코스옐로 진행 중 — 페이즈 6, 또는 FCY 하위 상태(mYellowFlagState 1..7).
    LMU는 페이즈가 그린으로 돌아온 뒤에도 리스타트 준비(6)를 잠깐 남긴다 (CrewChief LMU 매퍼가
    이 경우도 FCY로 친다). 상한 7은 부호 없는 바이트로 읽은 -1(=255, 무효)을 FCY로 세지 않게.
    C# RaceControlAnalyzer.FcyActive."""
    return phase == PHASE_FCY or Y_FCY_MIN <= yellow <= Y_FCY_MAX

MILESTONES_MIN = (60, 30, 10)      # 남은 시간 안내 시점 (분)


def _parse_penalty(text: str) -> Optional[tuple]:
    """
    게임 메시지에서 페널티 종류/사유 추출 → (종류, 사유) 또는 None.
    LMU/rF2 메시지는 영어 ("Drive Thru Penalty: Pit Lane Speeding" 등).
    키워드 매칭이라 새 문구가 나오면 여기에 추가한다.
    """
    tl = text.lower()
    if not any(k in tl for k in ("penalty", "drive thru", "drive-thru",
                                 "drive through", "stop/go", "stop go")):
        return None
    if "drive" in tl:
        kind = "drive-through"
    elif "stop" in tl:
        kind = "stop-and-go"
    elif "second" in tl or "sec " in tl or "time" in tl:
        kind = "time penalty"
    else:
        kind = "penalty"
    reason = ""
    if "pit" in tl and ("speed" in tl or "spd" in tl):
        reason = "pit lane speeding"
    elif "cut" in tl or "track limit" in tl or "boundar" in tl:
        reason = "track limits"
    elif "yellow" in tl or "full course" in tl or "caution" in tl:
        reason = "yellow flag infringement"
    elif "false start" in tl or "jump" in tl:
        reason = "start infringement"
    elif "contact" in tl or "avoidable" in tl:
        reason = "contact"
    elif "blocking" in tl:
        reason = "blocking"
    elif "rejoin" in tl:
        reason = "unsafe rejoin"
    return (kind, reason)
PIT_LIMIT_MARGIN_KMH = 5.0         # 리밋 + 이 이상 넘으면 경고
DEFAULT_PIT_LIMIT_KMH = 80.0       # Extended에서 리밋을 못 읽으면 이 값 사용


class RaceControlAnalyzer:
    def __init__(self, cfg):
        self.sector_calls = cfg.get("thresholds.sector_yellow_calls", True)
        self.reset()

    def reset(self) -> None:
        self._phase: Optional[int] = None
        self._yellow: Optional[int] = None
        self._prev_sector_flags: Optional[list] = None
        self._sector_yellow_announced: dict[int, float] = {}
        self._milestones_done: set[int] = set()
        self._initial_remaining: Optional[float] = None
        self._class_place: Optional[int] = None
        self._limiter_warned_t = 0.0
        self._blue_flag = False
        self._penalties: Optional[int] = None   # 미소화 페널티 수 (None=기준 미확보)
        self._recent_msgs: list = []            # (t, text) — 최근 게임 메시지
        self._last_msgs = {"status": "", "history": ""}
        self._pen_due: Optional[float] = None   # 페널티 콜 예정 시각 (메시지 대기)
        self._pen_count = 0
        self._post_lane_prev = False            # 직전 틱 피트레인 여부 (체커 뒤 피트인 엣지)
        self._post_garage = False               # 직전 틱 차고 여부 (체커 뒤 피트인 콜 무효화)

    # -- 5Hz 틱: 코스 상태 전이 ------------------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        ses = snap.session
        me = snap.player_scoring()
        if me is None:
            return
        phase = ses["game_phase"]
        yellow = ses["yellow_state"]

        if self._phase is not None and phase != self._phase:
            self._on_phase_change(self._phase, phase, state, snap, bus)
        if state.is_race and phase == PHASE_FCY \
                and self._yellow is not None and yellow != self._yellow:
            self._on_yellow_change(self._yellow, yellow, state, bus)
        self._phase = phase
        self._yellow = yellow

        # 내 피니시(또는 DNF) 뒤엔 블루 플래그·페널티를 말하지 않는다 (상태 추적은 계속).
        # 옐로와 피트 리미터는 쿨다운 랩·피트인에서도 안전 콜이다
        over = state.race_over_now(me)
        self._check_sector_yellow(ses, me, snap.t, bus)
        self._check_pit_limiter(ses, snap, bus)
        self._check_blue_flag(me, state, bus, over)
        self._collect_messages(ses, snap.t)
        self._check_penalties(me, snap.t, state, bus, over)
        if state.is_race and phase == PHASE_GREEN:
            self._check_time_milestones(ses, bus)
        if state.is_race:
            self._check_race_end_tick(state, snap, me, bus)
            self._check_post_race(state, snap, me, bus)

    def _on_phase_change(self, old: int, new: int, state: SessionState,
                         snap: Snapshot, bus: EventBus) -> None:
        # 레이스 스타트: 포메이션/카운트다운 → 그린
        if new == PHASE_GREEN and old in (3, 4) and state.is_race:
            bus.push(Event(
                type=EventType.RACE_START, priority=Priority.CRITICAL,
                data={"pool": "race_start"}, tone="urgent", ttl=6.0,
            ))
            state.add_narrative("(이벤트) 레이스 스타트")
        # FCY/세이프티카 발동
        elif new == PHASE_FCY:
            bus.push(Event(
                type=EventType.FCY, priority=Priority.CRITICAL,
                data={"pool": "fcy_start"}, tone="urgent", ttl=8.0,
                bridge={"topic": "풀코스옐로(세이프티카) 발동. 피트가 열리면 "
                                 "시간 손실 없이 피트할 기회. 연료/타이어 상태와 엮어 "
                                 "전략 조언을 해라."},
            ))
            state.set_issue("fcy", "풀코스옐로 진행 중 (피트 전략 기회)")
            state.add_narrative("(이벤트) FCY 발동")
        # FCY 해제 → 그린 (리스타트)
        elif new == PHASE_GREEN and old == PHASE_FCY:
            bus.push(Event(
                type=EventType.GREEN_FLAG, priority=Priority.CRITICAL,
                data={"pool": "green_flag"}, tone="urgent", ttl=6.0,
            ))
            state.clear_issue("fcy")
            state.add_narrative("(이벤트) 리스타트 그린")
        # 체커 게시 — 페이즈 8은 리더 피니시라 랩 다운·다른 클래스 차는 아직 달린다.
        # 내 체커 콜은 내 피니시(_push_race_end)에서
        elif new == PHASE_OVER and state.is_race:
            state.add_narrative("(이벤트) 체커 게시 (리더 피니시)")

    def _on_yellow_change(self, old: int, new: int, state: SessionState,
                          bus: EventBus) -> None:
        """FCY 중 피트 개방 전이 — 내구레이스에서 가장 돈이 되는 콜."""
        if old in (Y_PIT_CLOSED, Y_PIT_LEAD_LAP) and new == Y_PIT_OPEN:
            bus.push(Event(
                type=EventType.FCY_PIT_OPEN, priority=Priority.CRITICAL,
                data={"pool": "fcy_pit_open"}, tone="urgent", ttl=10.0,
                bridge={"topic": "FCY 중 피트가 방금 열렸다. 지금 들어오면 "
                                 "시간 손실이 최소다. 연료/타이어 상황 기준으로 "
                                 "들어올지 말지 판단을 말해라."},
            ))
            state.add_narrative("(이벤트) FCY 피트 오픈")

    def _check_sector_yellow(self, ses: dict, me: dict, now: float,
                             bus: EventBus) -> None:
        """
        섹터 로컬 옐로 — 엣지 트리거: 섹터 값이 '옐로 아님 → 옐로'로 바뀌는 순간만 콜한다.
        mSectorFlag[i]는 섹터 i+1이고 원시값 1이 그 섹터의 로컬 옐로다. 깨끗한 섹터는 LMU가 11,
        rF2 헤더 기준 0 — LMU의 깨끗함 11은 lmu-pitwall 인게임 측정(LMU 1.4)과 LmuOverlay 검증
        목록에 있고, CrewChief·TinyPedal·SimHub·pitwall 모두 '== 1'만 옐로로 본다. 그래서 1 말고는
        전부 옐로 아님이다 (0·11·2·7·255 등 — 2를 옐로로 본 자료는 확인 안 된 포럼 글뿐). 예전
        '0 이하 → 1..2' 규칙은 LMU의 11 → 1을 못 잡아 콜이 한 번도 안 나갔다. '== 1' 비교라
        파이썬 int와 C# byte, 부호 있는 -1과 부호 없는 255가 결과를 가르지 않는다.
        첫 샘플(리셋 직후)은 기록만 한다 — 붙기 전부터 켜져 있던 옐로는 언제 났는지 몰라 부르지 않는다.
        FCY 중(fcy_active)에는 콜하지 않는다: FCY·피트 오픈·그린 콜이 따로 있고, LMU가 FCY 동안
        섹터 값을 어떻게 쓰는지 측정한 자료가 없다 (전 섹터를 1로 올리면 FCY 콜에 섹터 콜이 겹친다 —
        CrewChief는 그린 페이즈에서만, LmuOverlay는 FCY 배너가 섹터 옐로보다 먼저).
        직전 값은 억제 중에도 매 틱 갱신한다 — FCY 동안 1이 된 섹터가 FCY가 끝나는 순간 가짜
        엣지로 나가지 않게. C# RaceControlAnalyzer.CheckSectorYellow.
        """
        if not self.sector_calls:
            return
        flags = list((ses.get("sector_flags") or [])[:3])
        prev = self._prev_sector_flags
        self._prev_sector_flags = flags
        if prev is None:
            return    # 첫 샘플: 이미 켜져 있던 플래그는 신뢰하지 않음
        if fcy_active(ses["game_phase"], ses["yellow_state"]):
            return    # FCY는 FCY 콜이 맡는다 (직전 값은 위에서 이미 갱신)
        for i, flag in enumerate(flags):
            was = prev[i] if i < len(prev) else None
            if was == SECTOR_FLAG_YELLOW or flag != SECTOR_FLAG_YELLOW:
                continue                       # 옐로 아님 → 옐로(1) 전이만
            last = self._sector_yellow_announced.get(i)
            if last is not None and now - last < SECTOR_YELLOW_REARM_S:
                continue
            self._sector_yellow_announced[i] = now
            bus.push(Event(
                type=EventType.SECTOR_YELLOW, priority=Priority.HIGH,
                message=msg("sector_yellow", n=i + 1),
                dedup_key=f"syellow_{i}", ttl=15.0, tone="urgent",
            ))

    def _check_blue_flag(self, me: dict, state: SessionState,
                         bus: EventBus, over: bool = False) -> None:
        """
        내게 블루 플래그가 게시된 순간(mFlag=6 엣지) 양보 안내.
        트래픽 분석기의 랩 델타 기반 콜과 상호 보완 — LMU가 mFlag를 안 채우면
        트래픽 쪽 랩핑 판정이, 채우면 이쪽이 먼저 잡는다 (쿨다운으로 중복 억제).
        """
        blue = bool(me.get("flag_blue"))
        if blue and not self._blue_flag and not over:
            bus.push(Event(
                type=EventType.BLUE_FLAG, priority=Priority.HIGH,
                data={"pool": "blue_flag"}, dedup_key="blue_flag",
                tone="urgent", ttl=6.0,
            ))
            state.add_narrative("(이벤트) 블루 플래그 — 랩 앞선 차에 양보")
        self._blue_flag = blue

    def _collect_messages(self, ses: dict, now: float) -> None:
        """게임 메시지 센터(Extended) 변화를 최근 목록에 쌓는다 (페널티 사유 파싱용)."""
        for key, field in (("status", "status_message"),
                           ("history", "history_message")):
            text = (ses.get(field) or "").strip()
            if text and text != self._last_msgs[key]:
                self._last_msgs[key] = text
                self._recent_msgs.append((now, text))
        # 10초 지난 메시지는 버린다
        self._recent_msgs = [(t, m) for t, m in self._recent_msgs if now - t <= 10.0]

    PENALTY_WAIT_SEC = 1.5    # 카운트 증가 후 종류 메시지가 뜰 시간을 기다림

    def _check_penalties(self, me: dict, now: float, state: SessionState,
                         bus: EventBus, over: bool = False) -> None:
        """
        미소화 페널티 수(mNumPenalties) 변화 감시.
          - 증가 → 게임 메시지에서 종류/사유를 파싱해 구체적으로 콜
            (메시지가 못 잡히면 일반 긴급 풀로 폴백)
          - 0으로 감소 → 소화 완료 안심 멘트 + 이슈 해제
        세션 중간 합류 시 첫 관측값은 기준으로만 쓰고 콜하지 않는다.
        """
        n = int(me.get("num_penalties", 0) or 0)
        if over:
            # 피니시 뒤엔 소화할 수도 없다 — 기준만 따라가고 대기 중인 콜도 버린다
            self._penalties = n
            self._pen_due = None
            return
        if self._penalties is None:
            self._penalties = n
            if n > 0:
                state.set_issue("penalty", f"미소화 페널티 {n}건")
            return
        if n > self._penalties:
            # 종류 메시지가 카운트보다 늦게 뜰 수 있어 잠깐 기다렸다 콜
            self._pen_due = now + self.PENALTY_WAIT_SEC
            self._pen_count = n
            state.set_issue("penalty", f"미소화 페널티 {n}건")
        elif n < self._penalties and n == 0:
            self._pen_due = None
            bus.push(Event(
                type=EventType.PENALTY, priority=Priority.NORMAL,
                message=msg("pen_clear"),
                dedup_key="pen_clear", ttl=20.0, tone="casual",
            ))
            state.clear_issue("penalty")
            state.add_narrative("(이벤트) 페널티 소화 완료")
        self._penalties = n

        if self._pen_due is not None and now >= self._pen_due:
            self._pen_due = None
            self._emit_penalty(self._pen_count, now, state, bus)

    def _emit_penalty(self, n: int, now: float, state: SessionState,
                      bus: EventBus) -> None:
        detail = None
        # 주의: 루프 변수를 msg로 쓰면 messages.msg 함수를 가린다 (실크래시)
        for _t, raw in reversed(self._recent_msgs):
            detail = _parse_penalty(raw)
            if detail:
                break
        if detail:
            kind, reason = detail
            message = penalty_text(kind, reason)
            issue = f"미소화 페널티 {n}건 ({kind}{', ' + reason if reason else ''})"
            topic = f"방금 페널티가 부여됐다: {kind}, 사유 {reason or '불명'}. " \
                    "다음 피트와 엮어 언제 소화할지 판단을 짧게."
        else:
            message = None                     # 일반 풀로 폴백
            issue = f"미소화 페널티 {n}건"
            topic = f"방금 페널티가 부여됐다 (미소화 {n}건). " \
                    "다음 피트와 엮어 언제 소화할지 판단을 짧게."
        bus.push(Event(
            type=EventType.PENALTY, priority=Priority.CRITICAL,
            data={"pool": "penalty"}, message=message,
            dedup_key=f"pen_{n}", tone="urgent", ttl=10.0,
            bridge={"topic": topic},
        ))
        state.set_issue("penalty", issue)
        state.add_narrative(f"(이벤트) 페널티 부여 — {issue}")

    def _check_pit_limiter(self, ses: dict, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p or not p.get("in_pitlane") or p.get("speed_limiter"):
            return
        limit = ses.get("pit_speed_limit") or 0.0
        if limit <= 1.0:
            limit = DEFAULT_PIT_LIMIT_KMH
        if p.get("speed_kmh", 0.0) > limit + PIT_LIMIT_MARGIN_KMH:
            if snap.t - self._limiter_warned_t < 10.0:
                return
            self._limiter_warned_t = snap.t
            bus.push(Event(
                type=EventType.PIT_LIMITER, priority=Priority.CRITICAL,
                data={"pool": "pit_limiter"}, tone="urgent", ttl=3.0,
            ))

    def _check_time_milestones(self, ses: dict, bus: EventBus) -> None:
        if ses["end_et"] <= 0:
            return
        remaining = ses["end_et"] - ses["current_et"]
        if remaining <= 0:
            return
        if self._initial_remaining is None:
            self._initial_remaining = remaining
        for minutes in MILESTONES_MIN:
            if minutes in self._milestones_done:
                continue
            # 레이스 총 길이보다 크거나 비슷한 마일스톤은 무의미 (1시간 레이스에 "1시간 남음" 방지)
            if minutes * 60 > self._initial_remaining - 120:
                self._milestones_done.add(minutes)
                continue
            if remaining <= minutes * 60:
                self._milestones_done.add(minutes)
                # 너무 늦게 붙은 경우(세션 중간 시작 등) 근접 마일스톤만 발화
                if remaining > (minutes * 60) - 90:
                    bus.push(Event(
                        type=EventType.RACE_MILESTONE, priority=Priority.NORMAL,
                        data={"remaining_min": minutes},
                        dedup_key=f"ms_{minutes}", ttl=45.0,
                    ))

    # -- 마지막 랩 / 내 피니시 (판단은 state.race_end) ---------------------------

    def _check_race_end_tick(self, state: SessionState, snap: Snapshot, me: dict,
                             bus: EventBus) -> None:
        # state.update 전에 돈다(race_end는 직전 틱 값). 이번 틱에 라인을 넘었으면 on_lap이 판단한다.
        re = state.race_end
        if re.lap is None or me["total_laps"] != re.lap or re.out:
            return
        fs = me["finish_status"]
        if fs == 1 or re.finished:
            self._push_race_end(state, snap, me, bus)   # 피니시 플래그가 크로싱 없이 늦게 켜진 경우 등
            return
        if fs != 0 or re.final_called_for == me["total_laps"]:
            return
        ses = snap.session
        if ses["game_phase"] not in RACING_PHASES:
            return
        if not flag_out(snap):
            # 내가 리더면 체커는 내 통과에서야 나온다 — 시계가 끝났으면 이번 랩이 마지막
            if not (0 < ses["end_et"] <= ses["current_et"]):
                return
            leader = race_leader(snap)
            if leader is None or leader["id"] != me["id"]:
                return
        # 예측이 빗나간 채 리더가 체커를 받았다(또는 내가 리더인데 시계가 끝났다) — 다음 라인 통과가 피니시.
        # 라인까지 몇 초 안 남았으면 '마지막 랩'과 체커 콜이 겹치니 생략
        pace = state.baseline_lap_time() or lap_pace(me)
        if pace and (1.0 - lap_frac(me, ses["current_et"], ses["track_len"], pace)) * pace \
                < LATE_MIN_TO_LINE_S:
            return
        self._push_final_lap(state, snap, me, bus)

    def _push_final_lap(self, state: SessionState, snap: Snapshot, me: dict,
                        bus: EventBus) -> None:
        lap = me["total_laps"]
        re = state.race_end
        # 클래스 선두면 선두 변형 풀 ("Final lap, P1. Nothing silly now.")
        pool = "final_lap_lead" if state.class_place_of(snap, me) == 1 else "final_lap"
        # 키는 랩마다 — 예측이 빗나가 다음 랩에 다시 부를 때 앞 콜 키에 막히지 않게.
        # 채널이 밀려 라인을 넘은 뒤에 나가면 안 된다 (안전망 콜은 라인 몇 초 전일 수 있다)
        if bus.push(Event(
                type=EventType.RACE_MILESTONE, priority=Priority.HIGH,
                data={"final_lap": True, "lap": lap + 1, "pool": pool},
                dedup_key=f"final_lap_{lap + 1}", ttl=30.0,
                valid_fn=lambda: state.race_end is re and not re.finished and re.lap == lap)):
            re.final_called_for = lap              # 수락됐을 때만 — 거절되면 안전망이 다시 시도
            state.add_narrative("(이벤트) 마지막 랩")

    def _push_race_end(self, state: SessionState, snap: Snapshot, me: dict,
                       bus: EventBus) -> None:
        re = state.race_end
        if re.end_called or not re.seen_unfinished:
            return
        place = state.class_place_of(snap, me)
        pool, start, gained = finish_call(place, re.start_class_place or 0, is_multiclass(snap))
        if bus.push(Event(
                type=EventType.RACE_END, priority=Priority.HIGH,
                data={"pool": pool, "class_place": place, "start_place": start,
                      "gained": gained}, ttl=30.0)):
            re.end_called = True
            re.end_t = snap.t
            state.add_narrative(f"(이벤트) 체커, 클래스 P{place} ({pool}, 스타트 P{start or '?'})")

    # -- 체커 뒤 무전: 쿨다운 랩 → 요약 → 피트인 ---------------------------------

    def _check_post_race(self, state: SessionState, snap: Snapshot, me: dict,
                         bus: EventBus) -> None:
        """
        체커 콜이 나간 뒤 실제 엔지니어처럼 이어간다 (레이스마다 한 번씩, 래치는 트래커에):
          - 체커 콜 + 7초: 쿨다운 랩 콜 — 아직 달리는 차가 있으면 라인 비키라는 경고,
            빠른 클래스가 남아 있으면 미러 경고 (이미 피트레인이면 생략)
          - + 20초: 요약 한 번 — 내 베스트 랩의 클래스 순위 (패스티스트 랩이면 그 말)
          - 체커 뒤 피트레인 진입 순간: 리미터·차고·인사 (진입하면 쿨다운 콜은 생략)
            ESC로 차고에 순간이동하면(in_pits·in_garage 같은 틱) 이미 세워 둔 차라 말없이 래치,
            차고에 들어간 뒤에는 대기 중인 피트인 콜도 버린다
        C# RaceControlAnalyzer.CheckPostRace.
        """
        p = snap.player or {}
        in_lane = bool(p.get("in_pitlane")) or bool(me["in_pits"])
        entered = in_lane and not self._post_lane_prev
        self._post_lane_prev = in_lane
        self._post_garage = bool(me["in_garage"])
        re = state.race_end
        if not re.end_called or re.end_t is None:
            return
        since = snap.t - re.end_t
        if entered and not re.pit_in_called and self._post_garage:
            # 차고로 순간이동 — "리미터 켜고 차고로"는 이미 세워 둔 차에 틀린 말 (쿨다운도 늦었다)
            re.pit_in_called = True
            re.cooldown_called = True
            state.add_narrative("(이벤트) 체커 뒤 차고로 바로 복귀 — 피트인 콜 생략")
        elif entered and not re.pit_in_called:
            if bus.push(Event(
                    type=EventType.POST_RACE_PIT, priority=Priority.HIGH,
                    data={"pool": "post_race_pit"}, ttl=15.0,
                    valid_fn=lambda: state.race_end is re and self._post_lane_prev
                    and not self._post_garage)):
                re.pit_in_called = True
                re.cooldown_called = True        # 이미 들어오는 중 — 쿨다운 콜은 늦었다
                state.add_narrative("(이벤트) 체커 뒤 피트인")
        if not re.cooldown_called and since >= POST_COOLDOWN_S - POST_EPS:
            if in_lane or me["in_garage"]:
                re.cooldown_called = True
            else:
                racing = [v for v in snap.vehicles
                          if v["id"] != me["id"] and v["finish_status"] == 0
                          and v["place"] > 0 and not v["in_pits"] and not v["in_garage"]]
                # 빠른 클래스 = 트래픽과 같은 판정 (계열 서열, 모르면 관측 페이스, 그것도 없으면 아님)
                pace = class_pace(snap.vehicles)
                faster = any(class_relation(me["cls"], v["cls"], pace) > 0 for v in racing)
                pool = cooldown_pool(len(racing), faster)
                if bus.push(Event(
                        type=EventType.COOLDOWN_LAP, priority=Priority.HIGH,
                        data={"pool": pool, "still_racing": len(racing)}, ttl=25.0,
                        valid_fn=lambda: state.race_end is re and not self._post_lane_prev)):
                    re.cooldown_called = True
                    state.add_narrative(f"(이벤트) 쿨다운 랩 ({pool})")
        if re.cooldown_called and not re.summary_called \
                and since >= POST_SUMMARY_S - POST_EPS:
            rank, timed = best_lap_rank(snap, me)
            pool = summary_pool(rank, timed, is_multiclass(snap))
            if bus.push(Event(
                    type=EventType.RACE_SUMMARY, priority=Priority.NORMAL,
                    data={"pool": pool, "rank": rank}, ttl=45.0,
                    valid_fn=lambda: state.race_end is re)):
                re.summary_called = True
                state.add_narrative(f"(이벤트) 레이스 요약 ({pool})")

    # -- 랩 완료: 클래스 순위 변동 ---------------------------------------------

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        me = snap.player_scoring()
        if me is None or not state.is_race:
            return
        cp = state.class_place_of(snap, me)
        prev = self._class_place
        self._class_place = cp
        re = state.race_end
        if state.race_over:
            if re.finished:
                self._push_race_end(state, snap, me, bus)
            return                       # 피니시 라인(과 그 뒤): 순위 변동 콜 대신 체커 콜 하나
        if re.final_lap and re.final_called_for != me["total_laps"]:
            self._push_final_lap(state, snap, me, bus)
        if prev is None or cp == prev or cp > CLASS_POS_MAX:
            return
        # 피트 사이클 중 순위 출렁임은 무시 (내가 피트 랩이면 침묵)
        if state.laps and state.laps[-1].in_pits:
            return
        if cp < prev:
            msg_pool, tone = "position_up", "casual"
        else:
            msg_pool, tone = "position_down", "casual"
        bus.push(Event(
            type=EventType.POSITION_CHANGE, priority=Priority.NORMAL,
            data={"pool": msg_pool, "class_place": cp},
            dedup_key=f"pos_{cp}", ttl=30.0, tone=tone,
        ))
        state.add_narrative(f"(이벤트) 클래스 P{prev}→P{cp}")
        state.set_issue("position", f"현재 클래스 P{cp}")
