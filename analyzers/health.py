"""
차량 컨디션 분석기 — 파손(충격/차체/부품 탈락)과 엔진/브레이크 온도.

파손 흐름 (판단하는 엔지니어의 핵심 — 드라이버가 아니라 툴이 확인한다):
  1. 충격 감지 (5Hz): mLastImpactET 변화 + 크기 임계값 → 즉시 데미지 콜
     (mDentSeverity로 대략적 부위 추정, 브리지로 LLM 후속)
  2. 충격 8초 뒤 자동 점검 리포트: 덴트 심각도/휠·타이어/공기압 누출을
     데이터로 훑고 결과를 불러준다 ("체크했어, 가벼운 자국뿐이야" /
     "우측 앞 공기압 빠지는 중, 박스 준비")
  3. 이후 랩에서 충격 전 페이스와 비교 → 유의미하게 느려졌으면
     "수리할지 말지" LLM 판단 트리거, 영향 없으면 안심 멘트 후 이슈 해제
  4. 피트 인 = 수리로 간주하고 데미지 이슈 리셋

충격과 무관한 상시 감시: 휠 탈락(즉시 콜), 슬로우 펑처(공기압 지속 하락).
임계값 초과 시에만 발화. 쿨다운으로 반복 억제.
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventBus, EventType, Priority
from state import SessionState
from telemetry import Snapshot

log = logging.getLogger("health")

# mDentSeverity 8개 존의 위치 근사 (정면부터 시계방향 관례).
# 정확한 존 매핑은 게임/차량마다 다를 수 있어 '대략적 위치'로만 사용한다.
DENT_ZONES = ["프론트", "프론트 우측", "우측", "리어 우측",
              "리어", "리어 좌측", "좌측", "프론트 좌측"]

REPAIR_PACE_DELTA = 0.5      # 데미지 후 랩당 이 이상 느려지면 수리 판단 트리거
NO_EFFECT_DELTA = 0.2        # 이 이하면 "영향 없음"으로 이슈 해제
OBSERVE_LAPS = 2             # 충격 후 관찰 랩 수

WHEEL_NAMES = ("왼쪽 앞", "오른쪽 앞", "왼쪽 뒤", "오른쪽 뒤")   # FL FR RL RR
REPORT_DELAY_SEC = 8.0       # 충격 후 자동 점검 리포트까지 대기
IMPACT_PRESSURE_DROP_KPA = 12.0   # 충격 전후 이 이상 빠지면 누출로 판단
# 슬로우 펑처: 세션 중 최고 공기압 대비 이 이상 하락 (온도에 따른 자연 변동
# ±10~20kPa를 넘는 값으로 설정해 오탐 방지)
SLOW_PUNCTURE_DROP_KPA = 25.0

# 프론트 윙 감시 — mFrontWingHeight의 충격 전후 지속적 변화 = 윙 손상.
# 고속(직선)에서만 샘플링해 코너 자세/연료에 의한 자연 변동을 배제한다.
# LMU 스포츠카(스플리터)에서 이 필드가 유효한지는 실차 검증 필요 —
# 정적이면 이 감지는 조용히 비활성. 리어 윙은 전용 필드가 없어
# 리어 존 덴트 + 부품 탈락 + 페이스 관찰로 커버한다.
WING_SAMPLE_SPEED_KMH = 150.0
WING_DROP_M = 0.010          # 10mm 이상 지속 하락이면 윙 손상 의심
WING_EMA_ALPHA = 0.03        # 5Hz 기준 ~7초 시정수

# 얼라인(조향 쏠림) 감시 — 서스펜션/스티어링 지오메트리가 틀어지면
# 직선에서도 조향을 한쪽으로 유지해야 한다. 고속·저조향 구간의 조향
# 입력 EMA를 충격 전후로 비교해 '실주행에 문제가 되는' 손상만 잡는다.
# (트랙 캠버/바람에 의한 상시 오프셋은 전후 비교로 상쇄된다.)
STEER_SAMPLE_SPEED_KMH = 150.0
STEER_SAMPLE_MAX = 0.20      # 이 이상 꺾인 샘플은 코너로 보고 제외
STEER_SHIFT_MIN = 0.03       # 락 대비 3% 이상 오프셋 변화면 얼라인 손상 의심
STEER_SHIFT_SEVERE = 0.08    # 이 이상이면 주행 자체가 힘든 수준 → 즉시 박스 권고
STEER_EMA_ALPHA = 0.02       # ~10초 시정수
STEER_JUDGE_SAMPLES = 75     # 충격 후 고속 샘플이 이만큼 쌓여 EMA가 안정된
                             # 뒤에 판정 (수렴 중 경미 단계를 스치는 오판 방지)

# 리어 불안정(오버스티어) 감시 — 리어 윙/서스가 죽으면 코너마다 미끄러진다.
# 판정은 '횡방향 속도'(mLocalVel.x): 정상 코너링은 1~2m/s, 진짜 슬라이드는
# 4m/s 이상. (요레이트+저조향 조합은 고속 코너를 슬라이드로 오인하고,
# 카운터 중엔 조향이 커져 진짜 슬라이드를 놓침 — 실차에서 확인된 오탐.)
#
# 드라이버 실수와의 구분: 슬라이드는 상시 카운트해서 '평소 빈도'를 기준선으로
# 잡고, 손상 후 빈도가 기준선의 2배(최소 3회)를 넘어야만 콜한다.
# 실수는 가끔, 손상은 코너마다 — 빈도 변화가 신호다.
INSTAB_WATCH_SEC = 180.0     # 손상 후 감시 시간
INSTAB_SPEED_KMH = 80.0
INSTAB_YAW_RAD_S = 0.55      # |요레이트| 동반 조건 (직선 휠스핀 배제)
INSTAB_LAT_SLIP_MS = 4.0     # |횡속도| 이 이상 = 실제 슬라이드
INSTAB_MIN_COUNT = 3         # 손상 후 최소 이 횟수 (실수 1~2회는 통과)
INSTAB_GAP_SEC = 3.0         # 같은 슬라이드를 중복 카운트하지 않는 간격


class HealthAnalyzer:
    def __init__(self, cfg):
        self.water_warn = cfg.get("thresholds.water_temp_warn", 105.0)
        self.oil_warn = cfg.get("thresholds.oil_temp_warn", 115.0)
        self.brake_warn = cfg.get("thresholds.brake_temp_warn", 700.0)
        self.impact_mag = cfg.get("thresholds.damage_impact_mag", 500.0)
        self.reset()

    def reset(self) -> None:
        self._last_impact_et = 0.0
        self._dents: Optional[list] = None
        self._impact_lap: Optional[int] = None
        self._pre_impact_pace: Optional[float] = None
        self._detached_warned = False
        self._report_due: Optional[float] = None    # 자동 점검 리포트 예정 시각
        self._pre_pressures: Optional[list] = None  # 충격 직전 휠 공기압
        self._wheel_detached_warned: set[int] = set()
        self._pressure_max = [0.0, 0.0, 0.0, 0.0]   # 세션/스틴트 중 최고 공기압
        self._puncture_warned: set[int] = set()
        self._was_in_pitlane = False
        self._wing_ema: Optional[float] = None      # 고속 구간 프론트 윙 높이 EMA
        self._pre_impact_wing: Optional[float] = None
        self._wing_warned = False
        self._steer_ema: Optional[float] = None     # 고속 직선 조향 오프셋 EMA
        self._pre_impact_steer: Optional[float] = None
        self._steer_samples = 0                     # 기준 확보 후 쌓인 고속 샘플 수
        self._align_warned = False
        self._last_impact_zone: Optional[str] = None
        self._instab_until = 0.0                    # 리어 불안정 감시 종료 시각
        self._instab_armed_t = 0.0                  # 감시 시작 시각
        self._instab_required = INSTAB_MIN_COUNT    # 기준선 반영된 콜 임계 횟수
        self._instab_last_t = 0.0
        self._instab_called = False
        self._slides: list[float] = []              # 슬라이드 시각 (상시 기록 — 기준선용)

    # -- 5Hz 틱: 충격/부품 탈락 즉시 감지 ---------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p:
            return

        wheels = p.get("wheels") or []

        # 피트레인 진입 = 타이어 교체/수리 가능성 → 공기압/윙 기준 리셋
        if p.get("in_pitlane"):
            if not self._was_in_pitlane:
                self._pressure_max = [0.0] * 4
                self._puncture_warned.clear()
                self._wheel_detached_warned.clear()
                self._wing_ema = None
                self._pre_impact_wing = None
                self._wing_warned = False
                self._steer_ema = None
                self._pre_impact_steer = None
                self._steer_samples = 0
                self._align_warned = False
                self._instab_until = 0.0
                self._instab_required = INSTAB_MIN_COUNT
                self._instab_called = False
            self._was_in_pitlane = True
        else:
            self._was_in_pitlane = False

        self._track_wing(p, state, bus)
        self._track_alignment(p, state, bus)

        impact_et = p.get("last_impact_et", 0.0)
        if impact_et and impact_et > self._last_impact_et + 0.5:
            prev_dents = self._dents
            self._dents = list(p.get("dent_severity") or [])
            self._last_impact_et = impact_et
            mag = p.get("last_impact_mag", 0.0)
            if mag >= self.impact_mag:
                self._pre_pressures = [w.get("pressure", 0.0) for w in wheels[:4]]
                if self._pre_impact_wing is None:    # 다중 충격 시 최초 기준 유지
                    self._pre_impact_wing = self._wing_ema
                if self._pre_impact_steer is None:
                    self._pre_impact_steer = self._steer_ema
                    self._steer_samples = 0
                self._report_due = snap.t + REPORT_DELAY_SEC
                self._on_impact(mag, prev_dents, snap.t, state, bus)

        # 충격 후 자동 점검 리포트 — 드라이버가 아니라 우리가 데이터로 확인
        if self._report_due is not None and snap.t >= self._report_due:
            self._report_due = None
            self._damage_report(p, state, bus)

        # 차체 부품 탈락 (미러/보닛/윙 등) — 펑크와 별개.
        # 직전 충격이 리어 쪽이면 리어 윙 가능성을 특정해서 부르고,
        # 어느 쪽이든 리어 불안정 감시를 켠다 (에어로 손실 대비).
        if p.get("detached") and not self._detached_warned:
            self._detached_warned = True
            if self._last_impact_zone and "리어" in self._last_impact_zone:
                msg = ("리어 부품 탈락. 리어 윙 가능성. "
                       "다음 코너 조심. 리어 돌면 바로 박스.")
                state.set_issue("damage", "리어 부품 탈락 — 리어 윙 손상 가능성")
            else:
                msg = "부품 탈락 감지. 에어로 영향 가능. 데이터 확인 중."
                state.set_issue("damage", "차체 부품 탈락 — 에어로 손상 의심")
            bus.push(Event(
                type=EventType.PART_DETACHED, priority=Priority.CRITICAL,
                message=msg, dedup_key="detached", tone="urgent", ttl=10.0,
            ))
            self._arm_instab(snap.t, "부품 탈락")
            if self._report_due is None:      # 점검 리포트가 예약 안 됐으면 예약
                self._report_due = snap.t + REPORT_DELAY_SEC

        # 휠 탈락 즉시 콜 (주행 불능급 — 페이스 관찰 없이 바로)
        for i, w in enumerate(wheels[:4]):
            if w.get("detached") and i not in self._wheel_detached_warned:
                self._wheel_detached_warned.add(i)
                bus.push(Event(
                    type=EventType.WHEEL_DAMAGE, priority=Priority.CRITICAL,
                    message=f"{WHEEL_NAMES[i]} 휠 나갔어! 스핀 조심. 천천히 피트로.",
                    dedup_key=f"wheel_det_{i}", tone="urgent", ttl=10.0,
                ))
                state.set_issue("damage", f"{WHEEL_NAMES[i]} 휠 탈락 — 즉시 피트 필요")

        self._check_slow_puncture(p, wheels, state, bus)
        self._check_instability(p, snap.t, state, bus)

    def _on_impact(self, mag: float, prev_dents: Optional[list], now: float,
                   state: SessionState, bus: EventBus) -> None:
        zone = self._new_dent_zone(prev_dents, self._dents)
        heavy = mag >= self.impact_mag * 4
        where = f"{zone} 쪽" if zone else "위치 불명"
        self._last_impact_zone = zone
        # 리어 쪽 충격이나 큰 충격이면 리어 불안정 감시 시작
        if heavy or (zone and "리어" in zone):
            self._arm_instab(now, f"{where}, 충격 {mag:.0f}")
        bus.push(Event(
            type=EventType.DAMAGE, priority=Priority.CRITICAL,
            data={"pool": "damage", "zone": zone or "", "mag": round(mag)},
            dedup_key="impact", tone="urgent", ttl=8.0,
            bridge={"topic": f"방금 {'큰 ' if heavy else ''}충격이 있었다 ({where}). "
                             "우리가 지금 데이터(휠/공기압/보디)를 점검 중이고 곧 결과를 "
                             "부를 예정. 드라이버는 페이스만 유지하면 된다고 안심시켜라. "
                             "드라이버에게 확인을 시키지 마라."},
        ))
        state.set_issue("damage", f"접촉 데미지 ({where}) — 페이스 영향 관찰 중")
        state.add_narrative(f"(이벤트) 충격 감지 ({where}, 크기 {mag:.0f})")
        self._impact_lap = len(state.laps)
        self._pre_impact_pace = state.baseline_lap_time()

    def _damage_report(self, p: dict, state: SessionState, bus: EventBus) -> None:
        """충격 몇 초 뒤 데이터를 훑어 결과를 불러준다 — 점검은 툴의 일."""
        wheels = p.get("wheels") or []
        problems: list[str] = []

        detached = [WHEEL_NAMES[i] for i, w in enumerate(wheels[:4]) if w.get("detached")]
        flats = [WHEEL_NAMES[i] for i, w in enumerate(wheels[:4])
                 if w.get("flat") and not w.get("detached")]
        if detached:
            problems.append(f"{'/'.join(detached)} 휠 손상")
        if flats:
            problems.append(f"{'/'.join(flats)} 펑크")

        # 충격 전후 공기압 비교 — 서서히 새는 누출 조기 발견
        if self._pre_pressures:
            for i, w in enumerate(wheels[:4]):
                if i in self._puncture_warned or w.get("flat") or w.get("detached"):
                    continue
                if i < len(self._pre_pressures) and self._pre_pressures[i] > 0 \
                        and self._pre_pressures[i] - w.get("pressure", 0.0) \
                        >= IMPACT_PRESSURE_DROP_KPA:
                    self._puncture_warned.add(i)
                    problems.append(f"{WHEEL_NAMES[i]} 공기압 빠지는 중")
        self._pre_pressures = None

        dents = list(p.get("dent_severity") or [])[:8]
        heavy_zones = [DENT_ZONES[i] for i, s in enumerate(dents) if s >= 2]
        light = any(s == 1 for s in dents)
        if heavy_zones:
            problems.append(f"{'/'.join(heavy_zones)} 보디 손상 심각")

        if problems:
            need_box = bool(detached or flats)
            advice = "박스 준비." if need_box else "페이스 보면서 가자. 수리 판단은 내가."
            message = f"체크 결과. {', '.join(problems)}. {advice}"
            state.set_issue("damage", "점검 결과: " + ", ".join(problems))
        elif light:
            message = "체크 완료. 가벼운 자국뿐. 휠, 타이어, 공기압 정상. 그대로 가."
        else:
            message = "체크 완료. 손상 없음, 깨끗해. 그대로 가."
        bus.push(Event(
            type=EventType.DAMAGE_REPORT, priority=Priority.HIGH,
            message=message, dedup_key=f"dmg_report_{self._last_impact_et}",
            ttl=20.0, tone="casual",
        ))
        state.add_narrative(f"(점검) {message}")

    def _track_wing(self, p: dict, state: SessionState, bus: EventBus) -> None:
        """
        프론트 윙 높이 감시 — 고속 직선에서만 EMA를 갱신하고, 충격 후
        같은 조건에서 기준 대비 지속적으로 낮아졌으면 윙 손상으로 특정한다.
        (연료 소모/코너 자세에 의한 자연 변동은 고속 한정 샘플링 + 10mm
        임계값으로 배제. 서스펜션 손상은 필드가 없어 페이스 관찰로 판단.)
        """
        h = p.get("front_wing_height")
        if not h or h <= 0 or p.get("in_pitlane") \
                or (p.get("speed_kmh", 0.0) or 0.0) < WING_SAMPLE_SPEED_KMH:
            return
        if self._wing_ema is None:
            self._wing_ema = h
            return
        self._wing_ema = (1 - WING_EMA_ALPHA) * self._wing_ema + WING_EMA_ALPHA * h

        if self._wing_warned or self._pre_impact_wing is None:
            return
        drop = self._pre_impact_wing - self._wing_ema
        if drop >= WING_DROP_M:
            # push가 쿨다운으로 거절되면 다음 틱에 재시도 (점검 리포트 직후 등)
            accepted = bus.push(Event(
                type=EventType.DAMAGE_REPORT, priority=Priority.HIGH,
                message=f"프론트 윙 {drop * 1000:.0f}밀리 하락. 윙 데미지. "
                        "고속 코너 조심. 수리는 페이스 보고 판단.",
                dedup_key="wing_damage", ttl=20.0, tone="casual",
            ))
            if not accepted:
                return
            self._wing_warned = True
            state.set_issue("damage", f"프론트 윙 손상 (높이 -{drop * 1000:.0f}mm)")
            state.add_narrative("(점검) 프론트 윙 손상 감지")

    def _track_alignment(self, p: dict, state: SessionState, bus: EventBus) -> None:
        """
        얼라인 감시 — 고속·저조향(직선) 구간의 조향 입력 평균이 충격 전과
        비교해 한쪽으로 옮겨가 있으면 지오메트리가 틀어진 것. 드라이버가
        느끼기 전에(또는 느낀 것을 확증해서) 불러준다.
        """
        steer = p.get("steering")
        if steer is None or p.get("in_pitlane") \
                or (p.get("speed_kmh", 0.0) or 0.0) < STEER_SAMPLE_SPEED_KMH \
                or abs(steer) > STEER_SAMPLE_MAX:
            return
        if self._steer_ema is None:
            self._steer_ema = steer
            return
        self._steer_ema = (1 - STEER_EMA_ALPHA) * self._steer_ema \
            + STEER_EMA_ALPHA * steer

        if self._align_warned or self._pre_impact_steer is None:
            return
        self._steer_samples += 1
        if self._steer_samples < STEER_JUDGE_SAMPLES:
            return    # EMA가 새 상태로 수렴할 때까지 판정 유보
        shift = self._steer_ema - self._pre_impact_steer
        if abs(shift) < STEER_SHIFT_MIN:
            return
        # 심각 단계: 주행 자체가 힘든 수준 → 페이스 관찰 없이 즉시 박스 권고
        if abs(shift) >= STEER_SHIFT_SEVERE:
            accepted = bus.push(Event(
                type=EventType.DAMAGE_REPORT, priority=Priority.CRITICAL,
                message="얼라인 심각. 직선에서도 조향 잡아야 하는 수준. "
                        "박스에서 수리하자.",
                dedup_key="align_severe", tone="urgent", ttl=15.0,
            ))
            if not accepted:
                return
            self._align_warned = True
            state.set_issue("damage", "얼라인 심각 손상 — 즉시 수리 권장")
            state.add_narrative("(점검) 얼라인 심각 손상 → 박스 콜")
            return
        # push가 쿨다운으로 거절되면 다음 틱에 재시도 (점검 리포트 직후 등)
        accepted = bus.push(Event(
            type=EventType.DAMAGE_REPORT, priority=Priority.HIGH,
            message="직선에서 핸들 쏠림. 충격으로 얼라인 틀어진 듯. "
                    "타이어 편마모 주의. 수리는 페이스 보고 판단.",
            dedup_key="align_damage", ttl=20.0, tone="casual",
        ))
        if not accepted:
            return
        self._align_warned = True
        state.set_issue("damage", "얼라인 틀어짐 의심 (직선 조향 오프셋 변화)")
        state.add_narrative("(점검) 얼라인 틀어짐 감지 — 직선 조향 오프셋 변화")

    def _check_instability(self, p: dict, now: float,
                           state: SessionState, bus: EventBus) -> None:
        """
        리어 불안정 감시 — 슬라이드(횡속도+요레이트 동반)는 상시 카운트해서
        드라이버의 '평소 빈도'를 기준선으로 삼고, 손상 후 빈도가 기준선을
        확실히 넘을 때만(2배, 최소 3회) 박스를 부른다. 드라이버 실수의
        가끔 슬라이드와 손상의 코너마다 슬라이드를 빈도 변화로 구분한다.
        """
        yaw = p.get("yaw_rate")
        lat_vel = p.get("lat_vel")
        if yaw is None or lat_vel is None or p.get("in_pitlane") \
                or (p.get("speed_kmh", 0.0) or 0.0) < INSTAB_SPEED_KMH:
            return
        if abs(yaw) < INSTAB_YAW_RAD_S or abs(lat_vel) < INSTAB_LAT_SLIP_MS:
            return
        if now - self._instab_last_t < INSTAB_GAP_SEC:
            return
        self._instab_last_t = now
        # 상시 기록 (기준선용). 오래된 것은 정리.
        self._slides.append(now)
        self._slides = [t for t in self._slides if now - t <= 2 * INSTAB_WATCH_SEC]

        if self._instab_called or now >= self._instab_until:
            return
        post = sum(1 for t in self._slides if t >= self._instab_armed_t)
        log.info("슬라이드 감지 (손상 후 %d/%d): 요레이트 %.2f, 횡속도 %.1fm/s",
                 post, self._instab_required, yaw, lat_vel)
        if post < self._instab_required:
            return
        self._instab_called = True
        bus.push(Event(
            type=EventType.DAMAGE_REPORT, priority=Priority.CRITICAL,
            message="리어 불안정 반복. 데미지 영향 같아. "
                    "무리 금지, 박스 권장.",
            dedup_key="rear_instab", tone="urgent", ttl=15.0,
        ))
        state.set_issue("damage", "리어 불안정 반복 (에어로/서스 손상 의심)")
        state.add_narrative("(점검) 손상 후 리어 불안정 반복 감지 → 박스 권장")

    def _arm_instab(self, now: float, reason: str) -> None:
        """
        리어 불안정 감시 시작 — 직전 3분간의 슬라이드 횟수를 기준선으로,
        콜에 필요한 손상 후 슬라이드 횟수를 정한다 (기준선×2, 최소 3회).
        이미 감시 중이면 창만 연장하고 기준선은 유지한다.
        """
        already = now < self._instab_until
        self._instab_until = now + INSTAB_WATCH_SEC
        if already:
            return
        baseline = sum(1 for t in self._slides
                       if now - INSTAB_WATCH_SEC <= t <= now)
        self._instab_armed_t = now
        self._instab_required = max(INSTAB_MIN_COUNT, baseline * 2)
        self._instab_called = False
        log.info("리어 불안정 감시 시작 (%s) — 기준선 %d회/3분, 콜 임계 %d회",
                 reason, baseline, self._instab_required)

    def _check_slow_puncture(self, p: dict, wheels: list,
                             state: SessionState, bus: EventBus) -> None:
        """충격과 무관한 상시 공기압 감시 — 최고치 대비 큰 하락 = 슬로우 펑처."""
        if p.get("in_pitlane") or (p.get("speed_kmh", 0.0) or 0.0) < 60:
            return    # 저속/피트에선 온도 하락으로 공기압이 자연히 떨어짐
        for i, w in enumerate(wheels[:4]):
            pr = w.get("pressure", 0.0)
            if pr <= 0:
                continue
            if pr > self._pressure_max[i]:
                self._pressure_max[i] = pr
            elif (self._pressure_max[i] - pr >= SLOW_PUNCTURE_DROP_KPA
                    and i not in self._puncture_warned and not w.get("flat")):
                self._puncture_warned.add(i)
                bus.push(Event(
                    type=EventType.TYRE_WARNING, priority=Priority.HIGH,
                    message=f"{WHEEL_NAMES[i]} 공기압 하락 중. 슬로우 펑처. "
                            "다음 피트에서 교체.",
                    dedup_key=f"slowpunc_{i}", ttl=20.0, tone="casual",
                ))
                state.set_issue("tyres", f"{WHEEL_NAMES[i]} 슬로우 펑처 의심")

    @staticmethod
    def _new_dent_zone(prev: Optional[list], cur: Optional[list]) -> Optional[str]:
        """새로 생기거나 심해진 덴트 존 → 대략적 위치 이름."""
        if not cur:
            return None
        worst_i, worst_delta = None, 0
        for i, sev in enumerate(cur[:8]):
            before = prev[i] if prev and i < len(prev) else 0
            if sev - before > worst_delta:
                worst_i, worst_delta = i, sev - before
        if worst_i is None:
            return None
        return DENT_ZONES[worst_i]

    def on_lap(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p:
            return

        self._check_damage_pace(state, bus)

        water = p.get("water_temp", 0.0)
        oil = p.get("oil_temp", 0.0)
        if p.get("overheating") or water >= self.water_warn or oil >= self.oil_warn:
            what = "수온" if water >= self.water_warn else "유온" if oil >= self.oil_warn else "엔진"
            bus.push(Event(
                type=EventType.ENGINE_WARNING, priority=Priority.HIGH,
                message=f"{what} 상승 중. 슬립스트림에서 나와서 공기 먹이자.",
                data={"water": water, "oil": oil}, ttl=30.0,
            ))
            state.set_issue("engine", f"엔진 온도 상승 (수온 {water:.0f}, 유온 {oil:.0f})")
        else:
            state.clear_issue("engine")

        wheels = p.get("wheels") or []
        if len(wheels) == 4:
            avg_brake = sum(w.get("brake_temp", 0.0) for w in wheels) / 4
            if avg_brake >= self.brake_warn:
                bus.push(Event(
                    type=EventType.BRAKE_WARNING, priority=Priority.NORMAL,
                    message=f"브레이크 평균 {avg_brake:.0f}도. "
                            "브레이킹 한 템포 일찍, 식히자.",
                    data={"avg_brake": round(avg_brake)}, ttl=30.0,
                ))

    def _check_damage_pace(self, state: SessionState, bus: EventBus) -> None:
        """충격 후 관찰 랩이 지나면 페이스 영향으로 수리 여부 판단."""
        if "damage" not in state.issues or self._impact_lap is None:
            return
        # 피트 인 = 수리로 간주하고 리셋
        if state.laps and state.laps[-1].in_pits:
            state.clear_issue("damage")
            self._impact_lap = None
            self._detached_warned = False
            return
        post = [l for l in state.laps[self._impact_lap:] if l.valid]
        if len(post) < OBSERVE_LAPS or self._pre_impact_pace is None:
            return
        post_avg = sum(l.lap_time for l in post[-OBSERVE_LAPS:]) / OBSERVE_LAPS
        delta = post_avg - self._pre_impact_pace

        if delta >= REPAIR_PACE_DELTA:
            bus.push(Event(
                type=EventType.LAP_ANALYSIS, priority=Priority.NORMAL,
                data={"triggers": [
                    f"접촉 데미지 이후 랩당 {delta:.1f}초 느려졌다. "
                    "피트에서 수리할지, 그냥 달릴지 판단해라 (수리는 시간 손실, "
                    "방치는 랩마다 손실 누적)."]},
                message=f"데미지로 랩당 {delta:.1f}초 손실. "
                        "다음 피트에 수리. 그게 이득.",
                dedup_key=f"repair_{self._impact_lap}",
            ))
            state.set_issue("damage", f"데미지로 랩당 {delta:.1f}초 손실 — 수리 권장")
            self._impact_lap = None    # 판단은 한 번만
        elif delta <= NO_EFFECT_DELTA:
            bus.push(Event(
                type=EventType.DAMAGE, priority=Priority.NORMAL,
                message="아까 접촉, 페이스 영향 없음. 신경 꺼도 돼.",
                dedup_key=f"dmg_ok_{self._impact_lap}", ttl=30.0,
            ))
            state.clear_issue("damage")
            self._impact_lap = None
