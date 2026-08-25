"""
트래픽 분석기 v2 — 차량별 상태 머신 (5Hz 매 틱 호출).

설계 원칙 (자연스러움의 핵심):
  - 각 상대 차량을 ID로 추적하고, 상태가 "전이될 때만" 발화한다.
    같은 상태 유지 중엔 침묵 → 같은 차 얘기가 서사처럼 이어진다:
    "뒤로 하이퍼카 붙는다" → (침묵) → "옆이야" → "지나갔어, 라인 복귀해도 돼"
  - 동시에 여러 대가 관련되면 개별 콜을 쏟아내지 않고 한 문장으로 종합한다.
    위협도 순으로 1~2대만 언급, 같은 클래스는 묶는다.
  - 긴급 근접 콜은 사전 캐시 풀(지연 0), 후속 설명은 브리지(LLM)로.

상태 머신:
    FAR → APPROACHING → NEARBY_BEHIND → ALONGSIDE → NEARBY_AHEAD → FAR
                              ↘ FAR (DROPPED: 떨어짐)
"""

from __future__ import annotations

import logging
import time
from dataclasses import dataclass, field
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("traffic")

# 상태 상수
FAR = "far"
APPROACHING = "approaching"
NEARBY_BEHIND = "nearby_behind"
NEARBY_AHEAD = "nearby_ahead"
ALONGSIDE = "alongside"

THREAT_RANK = {ALONGSIDE: 3, NEARBY_BEHIND: 2, APPROACHING: 1,
               NEARBY_AHEAD: 0, FAR: 0}

GREEN_PHASES = (5, 6)
MIN_CLOSING_MS = 2.0           # m/s — 이 이상 좁혀질 때만 '접근'
# 나란히 콜 리드 보정: 판정~재생까지의 지연(5Hz 샘플 + 큐 + 재생 시작)만큼
# 상대 접근 속도에 비례해 창을 미리 연다. 콜이 '들리는 순간'에 실제로
# 오버랩이 되도록. 느리게 머무는 차는 보정 0에 수렴.
CALL_LATENCY_SEC = 0.5
ALONGSIDE_LEAD_MAX_M = 5.0     # 리드 보정 상한 (과속 접근으로 창이 과도해지는 것 방지)
ETA_WINDOW = (0.0, 10.0)       # 접근 예고: 도달 예상 3~10초 (상한만 설정, 하한은 근접콜이 커버)
SIDE_LAT_MIN = 1.2             # 좌우 판정 최소 횡간격 (m) — 이하면 "옆"으로만
STATE_REANNOUNCE_SEC = 45.0    # 같은 차·같은 상태 재발화 최소 간격
BATTLE_GAP_SEC = 2.0           # 동클래스 ±2초 이내면 배틀 → 긴박 톤

# 정지/서행 차량 판정 — LMU가 고스트 처리하는 차(멈춤/서행/복귀 중)는
# 공유 메모리에 고스트 플래그가 없어서 속도로 추정해 배틀 콜에서 제외한다.
# 저속 코너(헤어핀 등)를 도는 정상 주행 차가 순간적으로 임계 아래로 내려가는
# 오탐이 실차에서 확인돼, '지속 시간' 조건을 함께 요구한다.
STOPPED_SPEED_MS = 12.0        # 이 이하로 움직이는 차는 '정지/서행' 후보 (43km/h)
STOPPED_PERSIST_SEC = 4.0      # 이 시간 이상 계속 저속이어야 정지/서행 확정
MY_RACING_SPEED_MS = 25.0      # 내가 이 이상으로 달릴 때만 위 판정 적용 (피트 오해 방지)
HAZARD_AHEAD_M = 250.0         # 전방 이 거리 안의 정지 차량은 위험 안내 1회
# 물리적 실존 필터 — 프라이빗 연습/퀄리에서 다른 참가자는 타이밍(lap_dist)만
# 갱신되고 내 트랙에는 물리적으로 없다. 미스폰/관전 슬롯도 마찬가지.
# 월드 좌표(mPos)가 이만큼 움직인 적이 있어야 실존 차량으로 취급한다 —
# lap_dist는 타이밍 전용 엔트리도 움직이므로 판정 기준이 못 된다.
MOVED_MIN_M = 15.0


def wrap_gap(delta_m: float, track_len: float) -> float:
    """lapDist 차이를 [-L/2, L/2) 부호 있는 거리로 보정. +면 내 앞."""
    half = track_len / 2.0
    return (delta_m + half) % track_len - half


# 클래스 서열 — LMU는 mEstimatedLapTime을 전 차량 동일한 트랙 기본값으로
# 채워둬서(실차 진단으로 확인) 랩타임 비교가 불가능하다. 클래스 이름의
# 서열로 상위 클래스(랩핑 트래픽)를 판정한다. 숫자가 클수록 빠른 클래스.
CLASS_RANK = [("hyper", 4), ("lmh", 4), ("lmdh", 4),
              ("lmp2", 3), ("gte", 2), ("gt3", 1)]


def class_rank(cls: str) -> int:
    c = (cls or "").lower()
    for key, rank in CLASS_RANK:
        if key in c:
            return rank
    return 0    # 서열 불명


@dataclass
class CarTrack:
    cid: int
    cls: str
    driver: str
    state: str = FAR
    gap_m: float = 0.0
    rate: Optional[float] = None       # dgap/dt EMA (m/s), +면 앞으로 이동(접근: 뒤차가 +)
    speed_est: Optional[float] = None  # 상대 절대속도 추정 (m/s) = 내 속도 + rate
    side: Optional[str] = None         # left | right | None
    faster: bool = False               # 랩핑 트래픽인가 (상위 클래스 or 랩 앞선 리더)
    lap_delta: float = 0.0             # 상대 진행도 - 내 진행도 (랩 단위, +면 앞섬)
    lapping: bool = False              # 동클래스인데 나를 랩 돌리러 오는 중
    backmarker: bool = False           # 내가 랩 돌리는(또는 하위 클래스) 트래픽
    slow_since: Optional[float] = None  # 저속 상태가 시작된 시각 (정지차 지속 판정)
    first_pos: Optional[list] = None   # 첫 관측 월드 좌표 (실존 필터 기준)
    first_lap_dist: Optional[float] = None  # pos 없는 데이터용 폴백 기준
    first_total_laps: int = 0
    moved: bool = False                # 관측 이후 물리적으로 움직인 적이 있는가
    seen: bool = False                 # 첫 관측을 마쳤는가 (첫 분류는 무발화)
    last_sample_t: float = 0.0
    announced: dict = field(default_factory=dict)   # state → 마지막 발화 시각
    engaged: bool = False              # 이 차에 대해 뭔가 말한 적 있는가 (서사 연속성)


class TrafficAnalyzer:
    def __init__(self, cfg):
        self.proximity_m = cfg.get("thresholds.proximity_m", 50.0)
        self.alongside_m = cfg.get("thresholds.alongside_m", 12.0)
        self.eta_warn = cfg.get("thresholds.traffic_eta_sec", 10.0)
        self.race_only = cfg.get("thresholds.traffic_race_only", False)
        # LMU의 mPathLateral 부호 방향이 가정과 반대면 true로 (좌우 콜 반전)
        self.side_invert = bool(cfg.get("thresholds.side_invert", False))
        # 스타트 직후 혼전 구간: 서사형 콜(접근/배틀/종합)은 전부 뒷북이 되므로
        # 끄고, 대신 '스포터 모드'로 좌우 점유 변화만 즉시 콜한다
        # ("왼쪽!", "양쪽에 차 있어", "왼쪽 클리어").
        self.start_spotter_sec = float(cfg.get("thresholds.start_spotter_sec", 45.0))
        self.tracks: dict[int, CarTrack] = {}
        self._hazard_announced: dict[int, float] = {}
        self._green_t: Optional[float] = None
        self._prev_phase: Optional[int] = None
        self._spot: Optional[dict] = None            # 스포터: 발화된 좌우 점유 상태
        self._spot_clear_since = {"left": None, "right": None}

    def reset(self) -> None:
        self.tracks.clear()
        self._hazard_announced.clear()
        self._green_t = None
        self._prev_phase = None
        self._spot = None
        self._spot_clear_since = {"left": None, "right": None}

    # -- 외부 조회 (브리지 유효성 검사 등에 사용) -----------------------------

    def car_state(self, cid: int) -> str:
        t = self.tracks.get(cid)
        return t.state if t else FAR

    def in_battle(self) -> bool:
        """동클래스 같은 랩 차량과 근접 경쟁 중인가 → 톤 태그 결정에 사용."""
        return any(t.state in (NEARBY_BEHIND, NEARBY_AHEAD, ALONGSIDE)
                   and not t.faster and not t.backmarker
                   for t in self.tracks.values())

    # -- 메인 틱 -------------------------------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        me = snap.player_scoring()
        if me is None or me["in_pits"] or me["in_garage"]:
            self.tracks.clear()
            return
        phase = snap.session["game_phase"]
        # 레이스 스타트(그린 전이) 감지 → 혼전 정숙 구간 시작
        if self._prev_phase in (3, 4) and phase == 5 and state.is_race:
            self._green_t = snap.t
        self._prev_phase = phase
        if phase not in GREEN_PHASES:
            return
        # 연습/퀄리는 고스트 차가 많아 트래픽 콜이 소음이 되기 쉬움 — 옵션으로 차단
        if self.race_only and not state.is_race:
            return
        track_len = snap.session["track_len"]
        if track_len <= 0:
            return
        now = snap.t
        # 스타트 직후 혼전: 서사형 콜 대신 스포터 모드 (좌우 점유만 즉시 콜)
        spotter_mode = (self._green_t is not None
                        and now - self._green_t < self.start_spotter_sec)
        my_speed = (snap.player.get("speed_kmh", 0.0) or 0.0) / 3.6

        transitions: list[tuple[CarTrack, str]] = []   # (track, old_state)
        seen: set[int] = set()

        for v in snap.vehicles:
            if v["is_player"] or v["in_pits"] or v["in_garage"] or v["finish_status"] != 0:
                continue
            seen.add(v["id"])
            t = self._update_track(v, me, track_len, now, my_speed)
            # 한 번도 움직인 적 없는 엔트리 = 미스폰/관전 슬롯 — 존재하지
            # 않는 것으로 취급 (프라이빗 연습/퀄리 유령 콜의 주범)
            if not t.moved:
                continue
            # 정지/서행 차량(고스트 처리됐을 가능성 높음): 배틀 콜 제외,
            # 레이스에서 전방이면 위험 안내 1회만 (연습/퀄리에선 게임이
            # 고스트 처리하므로 위험 안내도 소음)
            if self._is_stopped(t, my_speed, now):
                if state.is_race:
                    self._check_stopped_hazard(t, now, bus)
                if t.state != FAR:
                    t.state = FAR      # 조용히 리셋 (지나갔어/떨어졌어 멘트 없이)
                continue
            new_state = self._classify(t)
            if not t.seen:
                # 첫 관측: 현재 상태로 조용히 진입 (스타트 그리드/앱 중간 시작
                # 시점에 이미 근처인 차들을 '전이'로 오인해 쏟아내지 않는다)
                t.seen = True
                t.state = new_state
                continue
            if new_state != t.state:
                old = t.state
                t.state = new_state
                transitions.append((t, old))
        # 사라진 차량(피트인 등) 정리
        for cid in list(self.tracks):
            if cid not in seen:
                del self.tracks[cid]

        if spotter_mode:
            self._spotter_tick(now, bus)
        elif transitions:
            self._spot = None    # 스포터 모드 종료 → 기준 리셋
            self._emit(transitions, now, bus)

        # 레이스 서사 이슈: 동클래스 배틀 여부 (LLM 문맥 연속성용).
        # 랩 차이 나는 트래픽(백마커/랩핑)은 배틀이 아니다.
        battler = next((t for t in self.tracks.values()
                        if t.state in (NEARBY_BEHIND, ALONGSIDE)
                        and not t.faster and not t.backmarker), None)
        if battler is not None:
            state.set_issue("battle", f"{battler.cls} ({battler.driver})와 포지션 배틀 중")
        else:
            state.clear_issue("battle")

    # -- 스포터 모드 (스타트 혼전) --------------------------------------------

    SPOT_CLEAR_HOLD_SEC = 1.2    # 이 시간 이상 비어 있어야 '클리어' 콜 (깜빡임 방지)

    def _spotter_tick(self, now: float, bus: EventBus) -> None:
        """
        좌우 점유 변화만 즉시 콜하는 스포터 모드 — 첫 코너처럼 상황이 말보다
        빨리 변하는 구간용. 서사 대신 "왼쪽!" / "양쪽에 차 있어" / "왼쪽 클리어".
        창 시작 시점의 점유(그리드 옆 차)는 기준으로만 쓰고 콜하지 않는다.
        """
        occ = {"left": False, "right": False}
        for t in self.tracks.values():
            if t.state == ALONGSIDE and t.side in occ:
                occ[t.side] = True

        if self._spot is None:
            self._spot = dict(occ)
            return

        # 양쪽 동시 점유로 전이 → 한 번에 "양쪽" 콜
        if occ["left"] and occ["right"] \
                and not (self._spot["left"] and self._spot["right"]):
            if bus.push(Event(
                    type=EventType.SPOTTER, priority=Priority.CRITICAL,
                    data={"pool": "alongside_both"}, dedup_key="spot_both",
                    tone="urgent", ttl=2.5)):
                self._spot = {"left": True, "right": True}
                self._spot_clear_since = {"left": None, "right": None}
            return

        for side in ("left", "right"):
            if occ[side]:
                self._spot_clear_since[side] = None
                if not self._spot[side]:
                    if bus.push(Event(
                            type=EventType.SPOTTER, priority=Priority.CRITICAL,
                            data={"pool": f"alongside_{side}"},
                            dedup_key=f"spot_{side}", tone="urgent", ttl=2.5)):
                        self._spot[side] = True
            elif self._spot[side]:
                since = self._spot_clear_since[side]
                if since is None:
                    self._spot_clear_since[side] = now
                elif now - since >= self.SPOT_CLEAR_HOLD_SEC:
                    if bus.push(Event(
                            type=EventType.SPOTTER, priority=Priority.HIGH,
                            data={"pool": "side_clear", "side": side},
                            dedup_key=f"spotclr_{side}", tone="casual", ttl=3.0)):
                        self._spot[side] = False
                        self._spot_clear_since[side] = None

    def _is_stopped(self, t: CarTrack, my_speed: float, now: float) -> bool:
        """
        정지/서행 차량 추정 — 내가 레이싱 속도일 때만, 그리고 저속 상태가
        일정 시간 지속될 때만. 저속 코너를 정상 통과 중인 차는 잠깐만
        임계 아래로 내려가므로 지속 조건이 오탐을 걸러준다.
        """
        return (t.slow_since is not None and my_speed >= MY_RACING_SPEED_MS
                and now - t.slow_since >= STOPPED_PERSIST_SEC)

    def _check_stopped_hazard(self, t: CarTrack, now: float, bus: EventBus) -> None:
        """전방의 정지/서행 차량은 배틀 콜 대신 위험 안내 한 번만."""
        if not (0 < t.gap_m <= HAZARD_AHEAD_M):
            if t.gap_m < 0:
                self._hazard_announced.pop(t.cid, None)   # 지나가면 재안내 허용
            return
        last = self._hazard_announced.get(t.cid)
        if last is not None and now - last < 90.0:
            return
        self._hazard_announced[t.cid] = now
        from messages import msg
        bus.push(Event(
            type=EventType.TRAFFIC_UPDATE, priority=Priority.HIGH,
            message=msg("stopped_hazard"),
            dedup_key=f"hazard_{t.cid}", ttl=8.0, tone="urgent",
        ))

    def _update_track(self, v: dict, me: dict,
                      track_len: float, now: float, my_speed: float = 0.0) -> CarTrack:
        t = self.tracks.get(v["id"])
        if t is None:
            t = CarTrack(cid=v["id"], cls=v["cls"], driver=v["driver"])
            self.tracks[v["id"]] = t
        # 실존 필터: 월드 좌표가 실제로 움직인 적이 있는가. lap_dist는
        # 프라이빗 세션의 타이밍 전용 엔트리(트랙에 물리적으로 없는 차)도
        # 갱신되므로 기준이 못 된다 — 좌표가 안 움직이면 유령이다.
        pos = v.get("pos")
        if t.first_lap_dist is None:
            t.first_lap_dist = v["lap_dist"]
            t.first_total_laps = v["total_laps"]
            t.first_pos = list(pos) if pos and len(pos) >= 3 else None
        elif not t.moved:
            if t.first_pos is not None and pos and len(pos) >= 3:
                dx = pos[0] - t.first_pos[0]
                dy = pos[1] - t.first_pos[1]
                dz = pos[2] - t.first_pos[2]
                if dx * dx + dy * dy + dz * dz > MOVED_MIN_M ** 2:
                    t.moved = True
            elif (abs(v["lap_dist"] - t.first_lap_dist) > MOVED_MIN_M
                  or v["total_laps"] != t.first_total_laps):
                t.moved = True    # pos 없는 데이터(구버전 녹화) 폴백

        gap_m = wrap_gap(v["lap_dist"] - me["lap_dist"], track_len)
        dt = now - t.last_sample_t
        if t.last_sample_t > 0 and 0.01 < dt <= 3.0:
            inst = wrap_gap(gap_m - t.gap_m, track_len) / dt
            t.rate = inst if t.rate is None else (0.6 * t.rate + 0.4 * inst)
        elif dt > 3.0:
            t.rate = None      # 오래된 샘플로 미분하지 않는다
        # 상대 절대속도 추정: gap = 상대위치-내위치 이므로 d(gap)/dt = 상대속도-내속도
        t.speed_est = (my_speed + t.rate) if t.rate is not None else None
        # 저속 상태 지속 추적 (정지/서행 판정용)
        if t.speed_est is not None and t.speed_est < STOPPED_SPEED_MS:
            if t.slow_since is None:
                t.slow_since = now
        else:
            t.slow_since = None
        t.gap_m = gap_m
        t.last_sample_t = now
        # 랩 진행도 차이 — 랩핑/백마커 판정의 기준.
        # total_laps는 라인 통과 시 갱신되므로 lap_dist 비율을 더해 연속화한다.
        my_prog = me["total_laps"] + me["lap_dist"] / track_len
        their_prog = v["total_laps"] + v["lap_dist"] / track_len
        t.lap_delta = their_prog - my_prog

        # 랩핑 트래픽 판정: ① 상위 클래스(서열 비교) ② 동클래스라도 랩을
        # 앞선 리더가 돌리러 오는 경우 (블루 플래그 상황). 같은 랩의 동클래스는
        # 아무리 빨라도 배틀 상대 (양보 콜 대상 아님).
        # LMU의 mEstimatedLapTime은 전 차량 동일값이라 쓰지 않는다.
        mine, theirs = class_rank(me["cls"]), class_rank(v["cls"])
        if v["cls"] == me["cls"]:
            t.lapping = t.lap_delta >= 0.9
            t.faster = t.lapping
        else:
            t.lapping = False
            if mine > 0 and theirs > 0:
                t.faster = theirs > mine
            else:
                # 서열 불명인 낯선 클래스명 → 접근 예고 대상으로 취급
                # (실제 닫힘 속도 조건이 오탐을 걸러준다)
                t.faster = True
        # 내가 잡는 트래픽: 한 랩 이상 뒤졌거나 하위 클래스
        t.backmarker = (t.lap_delta <= -0.9
                        or (0 < theirs < mine))
        # 좌우 판정: 나란할 때만 의미 있음. mPathLateral 부호 방향은 근사라
        # 실차에서 반대로 나오면 config의 side_invert로 뒤집는다.
        lat = v.get("path_lat")
        my_lat = me.get("path_lat")
        if lat is not None and my_lat is not None and abs(lat - my_lat) >= SIDE_LAT_MIN:
            side = "left" if lat < my_lat else "right"
            if self.side_invert:
                side = "right" if side == "left" else "left"
            t.side = side
        else:
            t.side = None
        return t

    def _classify(self, t: CarTrack) -> str:
        g = t.gap_m
        # 나란히: 기준은 실제 차체 오버랩(차 길이), 접근 속도만큼 리드 보정
        lead = min(abs(t.rate or 0.0) * CALL_LATENCY_SEC, ALONGSIDE_LEAD_MAX_M)
        if abs(g) <= self.alongside_m + lead:
            return ALONGSIDE
        if -self.proximity_m <= g < 0:
            return NEARBY_BEHIND
        if 0 < g <= self.proximity_m:
            return NEARBY_AHEAD
        if g < 0 and t.rate is not None and t.rate >= MIN_CLOSING_MS:
            eta = -g / t.rate
            # 접근 예고는 '다른(빠른) 클래스' 랩핑 트래픽만. 같은 클래스의
            # 접근은 갭 추세/라이벌 인텔/근접 콜이 배틀 문맥으로 처리한다.
            if eta <= self.eta_warn and t.faster:
                return APPROACHING
        return FAR

    # -- 발화 ---------------------------------------------------------------

    def _emit(self, transitions: list[tuple[CarTrack, str]], now: float,
              bus: EventBus) -> None:
        # 이번 틱에 전이된 차들 중 발화 가치가 있는 것만 추림
        speak: list[tuple[CarTrack, str]] = []
        for t, old in transitions:
            if self._worth_announcing(t, old, now):
                speak.append((t, old))
        if not speak:
            return

        active = [t for t in self.tracks.values()
                  if THREAT_RANK[t.state] >= 1]

        if len(active) >= 2:
            self._emit_multi(active, now, bus)
            return
        t, old = max(speak, key=lambda p: THREAT_RANK[p[0].state])
        self._emit_single(t, old, now, bus)

    def _worth_announcing(self, t: CarTrack, old: str, now: float) -> bool:
        last = t.announced.get(t.state)
        if last is not None and now - last < STATE_REANNOUNCE_SEC:
            return False
        if t.state == ALONGSIDE:
            return True
        if t.state == NEARBY_BEHIND:
            return old in (FAR, APPROACHING)          # 뒤에서 붙은 경우만
        if t.state == APPROACHING:
            return old == FAR
        if t.state == NEARBY_AHEAD:
            if old == ALONGSIDE and t.engaged:        # 추월 완료 서사 마무리
                return True
            return t.backmarker and old == FAR        # 전방 백마커 예고
        if t.state == FAR:
            # 배틀하던 차가 떨어짐 → 서사가 있던 경우만 마무리 멘트
            return old in (NEARBY_BEHIND, ALONGSIDE) and t.engaged and not t.faster
        return False

    def _mark(self, t: CarTrack, now: float) -> None:
        t.announced[t.state] = now
        t.engaged = True

    @staticmethod
    def _rel_context(t: CarTrack) -> str:
        """브리지(LLM 후속)용 관계 설명 — 배틀인지 랩핑 트래픽인지."""
        if t.lapping:
            return "랩을 앞선 동클래스 리더가 랩 돌리러 온 상황 (블루 플래그, 양보 대상)"
        if t.faster:
            return "랩핑하러 온 상위 클래스 (무리해서 막을 필요 없음)"
        if t.backmarker:
            return "내가 랩 돌리는 백마커 (배틀 아님, 안전하게 추월)"
        return "동클래스 같은 랩 포지션 배틀 상황"

    def _emit_single(self, t: CarTrack, old: str, now: float, bus: EventBus) -> None:
        self._mark(t, now)
        tone = "urgent" if (t.state == ALONGSIDE or self.in_battle()) else "casual"
        cid = t.cid

        if t.state == ALONGSIDE:
            pool = {"left": "alongside_left", "right": "alongside_right"}.get(
                t.side, "alongside")
            bus.push(Event(
                type=EventType.TRAFFIC_CLOSE, priority=Priority.CRITICAL,
                data={"pool": pool, "cls": t.cls, "driver": t.driver},
                dedup_key=f"along_{cid}", ttl=3.0, tone="urgent",
                bridge={"topic": f"{t.cls} 차량({t.driver})이 지금 옆에 나란히 있다. "
                                 + self._rel_context(t)},
                valid_fn=lambda: self.car_state(cid) in (ALONGSIDE, NEARBY_BEHIND),
            ))
        elif t.state == NEARBY_BEHIND:
            bus.push(Event(
                type=EventType.TRAFFIC_CLOSE, priority=Priority.CRITICAL,
                data={"pool": "nearby_behind", "cls": t.cls, "driver": t.driver},
                dedup_key=f"near_{cid}", ttl=4.0, tone=tone,
                bridge={"topic": f"{t.cls} 차량이 뒤 50m 안에 붙었다. "
                                 + self._rel_context(t)},
                valid_fn=lambda: self.car_state(cid) in (NEARBY_BEHIND, ALONGSIDE),
            ))
        elif t.state == APPROACHING:
            eta = max(round(-t.gap_m / t.rate), 1) if t.rate else 4
            bus.push(Event(
                type=EventType.TRAFFIC_APPROACH, priority=Priority.CRITICAL,
                data={"cls": t.cls, "gap_sec": min(eta, 6)},
                dedup_key=f"appr_{cid}", ttl=5.0, tone=tone,
            ))
        elif t.state == NEARBY_AHEAD:
            if old == ALONGSIDE:           # 추월 완료 (내가 추월당함)
                bus.push(Event(
                    type=EventType.TRAFFIC_UPDATE, priority=Priority.HIGH,
                    data={"pool": "pass_complete", "cls": t.cls},
                    dedup_key=f"passed_{cid}", ttl=6.0, tone="casual",
                    valid_fn=lambda: self.car_state(cid) == NEARBY_AHEAD,
                ))
            else:                          # 전방 백마커 예고 (내가 잡는 트래픽)
                bus.push(Event(
                    type=EventType.TRAFFIC_UPDATE, priority=Priority.HIGH,
                    data={"pool": "backmarker_ahead", "cls": t.cls},
                    dedup_key=f"bm_{cid}", ttl=8.0, tone=tone,
                    bridge={"topic": f"전방에 백마커({t.cls}, 랩 차이 "
                                     f"{abs(t.lap_delta):.0f}랩)를 잡았다. "
                                     "무리 없는 추월 조언을 짧게."},
                    valid_fn=lambda: self.car_state(cid) in (NEARBY_AHEAD, ALONGSIDE),
                ))
        elif t.state == FAR:               # 배틀하던 차가 떨어짐
            bus.push(Event(
                type=EventType.TRAFFIC_UPDATE, priority=Priority.NORMAL,
                data={"pool": "dropped", "cls": t.cls},
                dedup_key=f"drop_{cid}", ttl=10.0, tone="casual",
                valid_fn=lambda: self.car_state(cid) == FAR,
            ))

    # -- 다중 차량 종합 -------------------------------------------------------

    def _emit_multi(self, active: list[CarTrack], now: float, bus: EventBus) -> None:
        for t in active:
            self._mark(t, now)
        message = self._compose_multi(active)
        if not message:
            return
        bus.push(Event(
            type=EventType.TRAFFIC_MULTI, priority=Priority.CRITICAL,
            message=message,
            dedup_key="multi", ttl=4.0, tone="urgent",
            bridge={"topic": "여러 대가 동시에 얽힌 트래픽 상황: " + message},
            valid_fn=lambda: sum(
                THREAT_RANK[t.state] >= 1 for t in self.tracks.values()) >= 2,
        ))

    def _compose_multi(self, active: list[CarTrack]) -> Optional[str]:
        """
        같은 상태의 차량들을 한 절로 합치고(클래스별 카운트), 위협도 순
        상위 2개 절만 한 문장으로. "뒤로 하이퍼카 하나, GT3 하나 붙는다" 식.
        """
        from messages import msg, class_display

        def names_of(cars: list[CarTrack]) -> str:
            counts: dict[str, int] = {}
            for c in cars:
                key = class_display(c.cls)
                counts[key] = counts.get(key, 0) + 1
            parts = []
            for name, n in counts.items():
                if n == 1:
                    parts.append(msg("multi_one", name=name))
                elif n == 2:
                    parts.append(msg("multi_two", name=name))
                else:
                    parts.append(msg("multi_n", name=name, n=n))
            return ", ".join(parts)

        by_state: dict[str, list[CarTrack]] = {}
        for t in active:
            by_state.setdefault(t.state, []).append(t)

        clauses: list[str] = []
        for st in (ALONGSIDE, NEARBY_BEHIND, APPROACHING):   # 위협도 순
            cars = by_state.get(st)
            if not cars or len(clauses) >= 2:
                continue
            if st == ALONGSIDE:
                key = {"left": "multi_alongside_left",
                       "right": "multi_alongside_right"}.get(
                    cars[0].side, "multi_alongside")
                clauses.append(msg(key, cls=class_display(cars[0].cls)))
            elif st == NEARBY_BEHIND:
                clauses.append(msg("multi_behind", names=names_of(cars)))
            else:
                clauses.append(msg("multi_closing", names=names_of(cars)))
        if not clauses:
            return None
        ahead_free = not any(t.state == NEARBY_AHEAD for t in active)
        tail = msg("multi_ahead_clear") if ahead_free and len(clauses) >= 2 else ""
        return ". ".join(clauses) + "." + tail
