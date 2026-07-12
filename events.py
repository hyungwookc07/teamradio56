"""
이벤트 버스: 분석기 → (우선순위 큐) → 보이스 워커.

분석기는 Event를 push만 한다. 큐가 다음을 책임진다:
  - 우선순위 정렬 (CRITICAL이 항상 먼저 나감)
  - 유형별 쿨다운 (같은 얘기 반복 방지 — 발화 억제 규칙의 일부)
  - 중복 억제 (같은 dedup_key가 대기 중이면 무시)
  - TTL (오래돼서 의미 없어진 이벤트 폐기 — 트래픽 콜 등)
"""

from __future__ import annotations

import heapq
import itertools
import logging
import threading
import time
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Callable, Optional

log = logging.getLogger("events")


class Priority(IntEnum):
    CRITICAL = 0    # 즉시 발화, 재생 중인 저우선순위 멘트 중단 가능
    HIGH = 1        # 다음 차례에 발화
    NORMAL = 2      # 여유 있을 때 발화 (LLM 멘트 등)


# 이벤트 타입 상수 (쿨다운 키로도 사용)
class EventType:
    FUEL_CRITICAL = "fuel_critical"
    FUEL_WARNING = "fuel_warning"
    PIT_CALL = "pit_call"
    TRAFFIC_APPROACH = "traffic_approach"
    TRAFFIC_CLOSE = "traffic_close"
    TRAFFIC_UPDATE = "traffic_update"       # 상태 전이 후속 (지나감/떨어짐)
    TRAFFIC_MULTI = "traffic_multi"         # 다중 차량 종합 한 문장
    BRIDGE_FOLLOWUP = "bridge_followup"     # 긴급 콜 뒤 LLM 후속 설명
    DAMAGE = "damage"
    PENALTY = "penalty"
    PACE_COMMENT = "pace_comment"
    GAP_COMMENT = "gap_comment"
    TYRE_WARNING = "tyre_warning"
    LAP_ANALYSIS = "lap_analysis"       # LLM 판단형 멘트 (v0.4)
    STINT_BRIEFING = "stint_briefing"   # LLM (v0.4)
    SESSION_BRIEFING = "session_briefing"  # 세션 시작 브리핑 (세션당 1회)
    RACE_START = "race_start"
    RACE_END = "race_end"
    LAP_FEEDBACK = "lap_feedback"       # 트레이닝: 섹터 델타 피드백
    TRACK_TREND = "track_trend"         # 트레이닝: 과거 세션 대비 추세
    FCY = "fcy"                         # 풀코스옐로/세이프티카 시작
    FCY_PIT_OPEN = "fcy_pit_open"       # FCY 중 피트 오픈 (전략 기회)
    GREEN_FLAG = "green_flag"           # 리스타트/그린
    SECTOR_YELLOW = "sector_yellow"     # 로컬 옐로
    BLUE_FLAG = "blue_flag"             # 블루 플래그 (랩 앞선 차에 양보)
    RACE_MILESTONE = "race_milestone"   # 남은 시간/랩 카운트다운, 마지막 랩
    POSITION_CHANGE = "position_change" # 클래스 순위 변동
    RIVAL_PIT = "rival_pit"             # 동클래스 경쟁자 피트 (언더컷/오버컷)
    RIVAL_PACE = "rival_pace"           # 라이벌 페이스 비교 인텔
    PIT_LIMITER = "pit_limiter"         # 피트레인 리미터 미작동 경고
    ENGINE_WARNING = "engine_warning"   # 수온/유온/과열
    BRAKE_WARNING = "brake_warning"     # 브레이크 온도
    FUEL_SAVE = "fuel_save"             # 연료 세이브 목표 코칭


# 타입 → 쿨다운 설정 키 매핑 (config.cooldowns)
COOLDOWN_KEY = {
    EventType.FUEL_CRITICAL: "fuel_warning",
    EventType.FUEL_WARNING: "fuel_warning",
    EventType.TRAFFIC_APPROACH: "traffic",
    EventType.TRAFFIC_CLOSE: "traffic_close",
    EventType.TRAFFIC_UPDATE: "traffic_update",
    EventType.TRAFFIC_MULTI: "traffic_multi",
    EventType.BRIDGE_FOLLOWUP: "bridge",
    EventType.PACE_COMMENT: "pace_comment",
    EventType.GAP_COMMENT: "gap_comment",
    EventType.TYRE_WARNING: "tyre_warning",
    EventType.DAMAGE: "damage",
    EventType.PENALTY: "penalty",
    EventType.FCY: "race_control",
    EventType.FCY_PIT_OPEN: "fcy_pit_open",   # 전용 버킷 — FCY 콜에 눌리면 안 됨
    EventType.GREEN_FLAG: "green_flag",
    EventType.SECTOR_YELLOW: "sector_yellow",
    EventType.BLUE_FLAG: "blue_flag",
    EventType.RACE_MILESTONE: "race_milestone",
    EventType.POSITION_CHANGE: "position_change",
    EventType.RIVAL_PIT: "rival_pit",
    EventType.RIVAL_PACE: "rival_pace",
    EventType.PIT_LIMITER: "pit_limiter",
    EventType.ENGINE_WARNING: "engine_warning",
    EventType.BRAKE_WARNING: "engine_warning",
    EventType.FUEL_SAVE: "fuel_save",
}

# CRITICAL 쿨다운 예외: 더 심각한 단계는 쿨다운 무시하고 1회 통과
DEFAULT_TTL = {
    Priority.CRITICAL: 8.0,     # 긴급 콜은 8초 지나면 의미 없음
    Priority.HIGH: 20.0,
    Priority.NORMAL: 45.0,
}


@dataclass
class Event:
    type: str
    priority: Priority
    data: dict = field(default_factory=dict)   # 멘트 생성에 쓸 가공된 값
    message: Optional[str] = None              # 이미 완성된 멘트 텍스트 (있으면 그대로 사용)
    dedup_key: Optional[str] = None            # 없으면 type이 dedup 키
    ttl: Optional[float] = None
    tone: str = "casual"                       # casual | urgent — 변형 풀 톤 선택
    bridge: Optional[dict] = None              # 긴급 콜 뒤 LLM 후속 생성 컨텍스트
    valid_fn: Optional[Callable[[], bool]] = None  # 재생 직전 유효성 검사 (False→폐기)
    created_at: float = field(default_factory=time.monotonic)

    @property
    def key(self) -> str:
        return self.dedup_key or self.type

    def expired(self, now: float) -> bool:
        ttl = self.ttl if self.ttl is not None else DEFAULT_TTL[self.priority]
        return now - self.created_at > ttl


class EventBus:
    def __init__(self, cooldowns: dict):
        self._cooldowns = cooldowns
        self._heap: list = []
        self._counter = itertools.count()
        self._pending_keys: set[str] = set()
        self._last_fired: dict[str, float] = {}   # 쿨다운키 → 마지막 수락 시각
        self._lock = threading.Lock()
        self._available = threading.Event()
        self.urgent_pending = threading.Event()   # 재생 중단 판단용

    def _cooldown_sec(self, etype: str) -> float:
        key = COOLDOWN_KEY.get(etype, etype)
        return float(self._cooldowns.get(key, self._cooldowns.get("default", 60)))

    def push(self, ev: Event) -> bool:
        """수락되면 True. 쿨다운/중복으로 버려지면 False."""
        now = time.monotonic()
        with self._lock:
            if ev.key in self._pending_keys:
                return False
            cd_key = COOLDOWN_KEY.get(ev.type, ev.type)
            last = self._last_fired.get(cd_key)
            if last is not None and now - last < self._cooldown_sec(ev.type):
                # CRITICAL은 같은 쿨다운 그룹의 하위 우선순위보다 한 단계 봐준다:
                # 쿨다운 절반이 지났으면 통과 (연료 warning 직후 critical 등)
                if not (ev.priority == Priority.CRITICAL
                        and now - last >= self._cooldown_sec(ev.type) * 0.5):
                    log.debug("쿨다운으로 무시: %s", ev.type)
                    return False
            self._last_fired[cd_key] = now
            self._pending_keys.add(ev.key)
            heapq.heappush(self._heap, (int(ev.priority), next(self._counter), ev))
            self._available.set()
            if ev.priority == Priority.CRITICAL:
                self.urgent_pending.set()
        log.debug("이벤트 수락: %s (%s)", ev.type, ev.priority.name)
        return True

    def pop(self, timeout: float = 0.5) -> Optional[Event]:
        """우선순위가 가장 높은 유효 이벤트를 꺼낸다. 없으면 None."""
        if not self._available.wait(timeout):
            return None
        now = time.monotonic()
        with self._lock:
            while self._heap:
                _, _, ev = heapq.heappop(self._heap)
                self._pending_keys.discard(ev.key)
                if ev.expired(now):
                    log.debug("TTL 만료로 폐기: %s", ev.type)
                    continue
                if ev.valid_fn is not None:
                    try:
                        if not ev.valid_fn():
                            log.debug("상황 종료로 폐기: %s", ev.type)
                            continue
                    except Exception:
                        pass  # 유효성 검사 실패는 재생 강행보다 안전한 쪽(폐기 안 함)
                if not self._heap:
                    self._available.clear()
                if not any(e.priority == Priority.CRITICAL for _, _, e in self._heap):
                    self.urgent_pending.clear()
                return ev
            self._available.clear()
            self.urgent_pending.clear()
        return None

    def clear(self) -> None:
        """세션 경계에서 호출 — 대기 이벤트와 쿨다운 기록을 모두 비운다."""
        with self._lock:
            self._heap.clear()
            self._pending_keys.clear()
            self._last_fired.clear()
            self._available.clear()
            self.urgent_pending.clear()
