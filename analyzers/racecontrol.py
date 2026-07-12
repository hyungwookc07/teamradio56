"""
레이스 컨트롤 분석기 — 코스 상태/레이스 진행 (5Hz 매 틱 + 랩 완료 시 호출).

담당:
  - FCY(풀코스옐로)/세이프티카 진입·해제, FCY 중 피트 오픈 (내구레이스 전략 핵심)
  - 섹터 로컬 옐로
  - 레이스 스타트(그린)/체커, 남은 시간 마일스톤, 마지막 랩
  - 피트레인 리미터 미작동 경고
  - 클래스 순위 변동 콜
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("racecontrol")

# rF2 게임 페이즈
PHASE_GREEN = 5
PHASE_FCY = 6
PHASE_OVER = 8
# rF2 옐로 상태 (mYellowFlagState)
Y_PIT_CLOSED = 2
Y_PIT_LEAD_LAP = 3
Y_PIT_OPEN = 4

MILESTONES_MIN = (60, 30, 10)      # 남은 시간 안내 시점 (분)
PIT_LIMIT_MARGIN_KMH = 5.0         # 리밋 + 이 이상 넘으면 경고
DEFAULT_PIT_LIMIT_KMH = 80.0       # Extended에서 리밋을 못 읽으면 이 값 사용


class RaceControlAnalyzer:
    def __init__(self, cfg):
        self.sector_calls = cfg.get("thresholds.sector_yellow_calls", True)
        self._phase: Optional[int] = None
        self._yellow: Optional[int] = None
        self._prev_sector_flags: Optional[list] = None
        self._sector_yellow_announced: dict[int, float] = {}
        self._milestones_done: set[int] = set()
        self._initial_remaining: Optional[float] = None
        self._final_lap_done = False
        self._race_started = False
        self._class_place: Optional[int] = None
        self._limiter_warned_t = 0.0

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

        self._check_sector_yellow(ses, me, snap.t, bus)
        self._check_pit_limiter(ses, snap, bus)
        if state.is_race and phase == PHASE_GREEN:
            self._check_time_milestones(state, ses, bus)

    def _on_phase_change(self, old: int, new: int, state: SessionState,
                         snap: Snapshot, bus: EventBus) -> None:
        # 레이스 스타트: 포메이션/카운트다운 → 그린
        if new == PHASE_GREEN and old in (3, 4) and state.is_race:
            self._race_started = True
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
        # 체커
        elif new == PHASE_OVER and state.is_race and self._race_started:
            place = self._class_place or (state.laps[-1].class_place if state.laps else 0)
            bus.push(Event(
                type=EventType.RACE_END, priority=Priority.HIGH,
                data={"pool": "race_end", "class_place": place}, ttl=30.0,
            ))
            state.add_narrative(f"(이벤트) 체커, 클래스 P{place}")

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
        엣지 트리거: 플래그가 0 → 양수로 '바뀌는 순간'만 콜한다.
        LMU가 mSectorFlag를 제대로 안 채우거나 값이 계속 박혀 있는 경우
        (앱 시작 시점부터 켜져 있던 값 포함) 반복 콜을 내지 않기 위함.
        비정상적으로 큰 값(>2)은 쓰레기 데이터로 보고 무시한다.
        """
        if not self.sector_calls:
            return
        flags = list((ses.get("sector_flags") or [])[:3])
        prev = self._prev_sector_flags
        self._prev_sector_flags = flags
        if prev is None:
            return    # 첫 샘플: 이미 켜져 있던 플래그는 신뢰하지 않음
        for i, flag in enumerate(flags):
            was = prev[i] if i < len(prev) else 0
            if not (was <= 0 < flag <= 2):     # 0→(1|2) 전이만 유효
                continue
            last = self._sector_yellow_announced.get(i)
            if last is not None and now - last < 120:
                continue
            self._sector_yellow_announced[i] = now
            bus.push(Event(
                type=EventType.SECTOR_YELLOW, priority=Priority.HIGH,
                message=f"섹터{i + 1} 옐로야. 사고 지점 지날 때 추월 금지, 감속 준비.",
                dedup_key=f"syellow_{i}", ttl=15.0, tone="urgent",
            ))

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

    def _check_time_milestones(self, state: SessionState, ses: dict,
                               bus: EventBus) -> None:
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
        # 마지막 랩: 남은 시간이 평소 랩타임보다 짧아진 순간
        base = state.baseline_lap_time()
        if not self._final_lap_done and base and remaining < base:
            self._final_lap_done = True
            bus.push(Event(
                type=EventType.RACE_MILESTONE, priority=Priority.HIGH,
                data={"final_lap": True}, message="이번이 마지막 랩이야. 다 쏟아붓자.",
                dedup_key="final_lap", ttl=30.0,
            ))
            state.add_narrative("(이벤트) 마지막 랩")

    # -- 랩 완료: 클래스 순위 변동 ---------------------------------------------

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        me = snap.player_scoring()
        if me is None or not state.is_race:
            return
        cp = state.class_place_of(snap, me)
        prev = self._class_place
        self._class_place = cp
        if prev is None or cp == prev:
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
