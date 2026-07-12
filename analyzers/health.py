"""
차량 컨디션 분석기 — 파손(충격/차체/부품 탈락)과 엔진/브레이크 온도.

파손 흐름 (판단하는 엔지니어의 핵심):
  1. 충격 감지 (5Hz): mLastImpactET 변화 + 크기 임계값 → 즉시 데미지 콜
     (mDentSeverity로 대략적 부위 추정, 브리지로 LLM 후속)
  2. 이후 랩에서 충격 전 페이스와 비교 → 유의미하게 느려졌으면
     "수리할지 말지" LLM 판단 트리거, 영향 없으면 안심 멘트 후 이슈 해제
  3. 피트 인 = 수리로 간주하고 데미지 이슈 리셋

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

    # -- 5Hz 틱: 충격/부품 탈락 즉시 감지 ---------------------------------------

    def on_tick(self, state: SessionState, snap: Snapshot, bus: EventBus) -> None:
        p = snap.player
        if not p:
            return

        impact_et = p.get("last_impact_et", 0.0)
        if impact_et and impact_et > self._last_impact_et + 0.5:
            prev_dents = self._dents
            self._dents = list(p.get("dent_severity") or [])
            self._last_impact_et = impact_et
            mag = p.get("last_impact_mag", 0.0)
            if mag >= self.impact_mag:
                self._on_impact(mag, prev_dents, state, bus)

        # 차체 부품 탈락 (미러/보닛/윙 등) — 펑크와 별개
        if p.get("detached") and not self._detached_warned:
            self._detached_warned = True
            bus.push(Event(
                type=EventType.DAMAGE, priority=Priority.CRITICAL,
                message="차에서 부품 떨어졌어. 에어로 영향 있을 거야, 감각 이상하면 바로 말해.",
                dedup_key="detached", tone="urgent", ttl=10.0,
            ))
            state.set_issue("damage", "차체 부품 탈락 — 에어로 손상 의심")

    def _on_impact(self, mag: float, prev_dents: Optional[list],
                   state: SessionState, bus: EventBus) -> None:
        zone = self._new_dent_zone(prev_dents, self._dents)
        heavy = mag >= self.impact_mag * 4
        where = f"{zone} 쪽" if zone else "위치 불명"
        bus.push(Event(
            type=EventType.DAMAGE, priority=Priority.CRITICAL,
            data={"pool": "damage", "zone": zone or "", "mag": round(mag)},
            dedup_key="impact", tone="urgent", ttl=8.0,
            bridge={"topic": f"방금 {'큰 ' if heavy else ''}충격이 있었다 ({where}). "
                             "다음 랩 페이스를 보고 수리 여부를 판단할 예정. "
                             "드라이버를 안심시키고 뭘 체크해야 하는지 조언해라."},
        ))
        state.set_issue("damage", f"접촉 데미지 ({where}) — 페이스 영향 관찰 중")
        state.add_narrative(f"(이벤트) 충격 감지 ({where}, 크기 {mag:.0f})")
        self._impact_lap = len(state.laps)
        self._pre_impact_pace = state.baseline_lap_time()

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
                message=f"{what}이 올라가고 있어. 앞차 슬립스트림에서 나와서 공기 좀 먹이자.",
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
                    message=f"브레이크가 평균 {avg_brake:.0f}도야. "
                            "브레이킹 한 템포 일찍 시작해서 식히자.",
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
                message=f"데미지 때문에 랩당 {delta:.1f}초씩 새고 있어. "
                        "다음 피트 때 수리하자, 계산해보면 그게 이득이야.",
                dedup_key=f"repair_{self._impact_lap}",
            ))
            state.set_issue("damage", f"데미지로 랩당 {delta:.1f}초 손실 — 수리 권장")
            self._impact_lap = None    # 판단은 한 번만
        elif delta <= NO_EFFECT_DELTA:
            bus.push(Event(
                type=EventType.DAMAGE, priority=Priority.NORMAL,
                message="페이스 보니까 아까 접촉은 영향 없어. 신경 쓰지 말고 가자.",
                dedup_key=f"dmg_ok_{self._impact_lap}", ttl=30.0,
            ))
            state.clear_issue("damage")
            self._impact_lap = None
