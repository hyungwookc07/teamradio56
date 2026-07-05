"""
멘트 생성 — 이원화 설계.

  1) 긴급 콜: 사전 생성 변형 풀 (v0.3) → 지연 0
  2) 비긴급 멘트: 실시간 LLM (v0.4) → 3~5초 지연 허용

v0.2 현재는 두 경로 모두 한국어 템플릿으로 동작한다. 텍스트가 None이면
발화하지 않는다 (발화 억제).
"""

from __future__ import annotations

import logging
from typing import Optional

from events import Event, EventType

log = logging.getLogger("voice")


class VoiceGenerator:
    def __init__(self, cfg, state):
        self.cfg = cfg
        self.state = state

    def text_for(self, ev: Event) -> Optional[str]:
        if ev.message:
            return ev.message
        renderer = getattr(self, f"_render_{ev.type}", None)
        if renderer is None:
            log.debug("렌더러 없는 이벤트 무시: %s", ev.type)
            return None
        return renderer(ev.data)

    # -- 긴급 콜 (v0.3에서 변형 풀로 교체) -----------------------------------

    def _render_fuel_critical(self, d: dict) -> str:
        return f"연료 심각해. {d['fuel_laps']:.0f}랩이면 바닥이야. 바로 박스 준비해."

    def _render_fuel_warning(self, d: dict) -> str:
        return f"연료 앞으로 {d['fuel_laps']:.0f}랩 분량이야. 피트 계획 잡자."

    def _render_pit_call(self, d: dict) -> str:
        return "박스 박스, 박스 박스. 이번 랩에 들어와."

    def _render_damage(self, d: dict) -> str:
        return "접촉 확인. 차 상태 어때? 데이터 보고 있어."

    def _render_penalty(self, d: dict) -> str:
        return f"페널티 떴어. {d.get('count', 1)}개 미소화야. 처리 타이밍 잡아줄게."

    # -- 비긴급 멘트 (v0.4에서 LLM으로 교체) ---------------------------------

    def _render_pace_comment(self, d: dict) -> Optional[str]:
        delta = abs(d["delta"])
        if d.get("direction") == "slower":
            return f"방금 랩, 평소보다 {delta:.1f}초 느렸어. 어디서 잃었는지 확인해봐."
        return f"좋아, 평소보다 {delta:.1f}초 빨라. 이 리듬 유지하자."

    def _render_gap_comment(self, d: dict) -> Optional[str]:
        rate = d["rate"]
        gap = d["gap"]
        if d["who"] == "behind":
            if rate < 0:
                return f"뒤차가 랩당 {abs(rate):.1f}초씩 붙고 있어. 갭 {gap:.0f}초. 서두르지 말고 실수만 줄이자."
            return f"뒤차랑 갭이 {gap:.0f}초로 벌어졌어. 관리 잘 되고 있어."
        if rate < 0:
            return f"앞차가 느려지고 있어. 갭 {gap:.0f}초, 랩당 {abs(rate):.1f}초씩 좁혀져. 갈 수 있어."
        return f"앞차랑 {gap:.0f}초로 벌어지는 중이야. 무리하진 말자."
