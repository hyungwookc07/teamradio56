"""
멘트 생성 — 이원화 설계.

  1) 긴급 콜: voice_lines/urgent_ko.yaml의 사전 생성 변형 풀에서 선택.
     랜덤 + 최근 사용 이력 제외로 반복감 방지. tools/pregen_audio.py로
     오디오까지 사전 캐시하면 지연 0.
  2) 비긴급 멘트: 실시간 LLM (v0.4) — 현재는 한국어 템플릿.

텍스트가 None이면 발화하지 않는다 (발화 억제).
"""

from __future__ import annotations

import logging
import os
import random
from collections import deque
from typing import Optional

import yaml

from events import Event, EventType

log = logging.getLogger("voice")

URGENT_LINES_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                 "voice_lines", "urgent_ko.yaml")

# 게임 클래스명 → 멘트에서 부르는 이름
CLASS_KO = {
    "Hypercar": "하이퍼카",
    "LMH": "하이퍼카",
    "LMDh": "하이퍼카",
    "LMP2": "엘엠피 투",
    "LMGT3": "GT3",
    "GT3": "GT3",
    "GTE": "GTE",
}


def class_ko(cls: str) -> str:
    for key, name in CLASS_KO.items():
        if key.lower() in cls.lower():
            return name
    return "상위 클래스"


class PhrasePool:
    """이벤트 타입(풀 키)별 변형 멘트 풀. 최근 사용 이력을 피해서 뽑는다."""

    RECENT_EXCLUDE = 5   # 같은 풀에서 최근 N개는 다시 안 씀

    def __init__(self, path: str = URGENT_LINES_FILE):
        self.pools: dict[str, list[str]] = {}
        self._recent: dict[str, deque] = {}
        try:
            with open(path, "r", encoding="utf-8") as f:
                self.pools = yaml.safe_load(f) or {}
            log.info("멘트 풀 로드: %d개 타입, 총 %d개 변형",
                     len(self.pools), sum(len(v) for v in self.pools.values()))
        except OSError as e:
            log.warning("멘트 풀 로드 실패(%s) — 템플릿 폴백", e)

    def pick(self, pool_key: str, slots: dict) -> Optional[str]:
        pool = self.pools.get(pool_key)
        if not pool:
            return None
        recent = self._recent.setdefault(
            pool_key, deque(maxlen=min(self.RECENT_EXCLUDE, max(len(pool) - 1, 1))))
        candidates = [p for p in pool if p not in recent] or pool
        phrase = random.choice(candidates)
        recent.append(phrase)
        try:
            return phrase.format(**slots)
        except (KeyError, IndexError) as e:
            log.warning("멘트 슬롯 오류 [%s] %r: %s", pool_key, phrase, e)
            return None


class VoiceGenerator:
    def __init__(self, cfg, state):
        self.cfg = cfg
        self.state = state
        self.pool = PhrasePool()

    def text_for(self, ev: Event) -> Optional[str]:
        if ev.message:
            return ev.message
        renderer = getattr(self, f"_render_{ev.type}", None)
        if renderer is None:
            log.debug("렌더러 없는 이벤트 무시: %s", ev.type)
            return None
        return renderer(ev.data)

    # -- 긴급 콜: 사전 생성 변형 풀 ------------------------------------------
    # 슬롯 값은 반드시 이산화(정수/고정 문자열)한다. 사전 캐시 히트 조건.

    def _render_traffic_approach(self, d: dict) -> Optional[str]:
        return self.pool.pick("traffic_approach", {
            "cls": class_ko(d["cls"]),
            "gap": int(min(max(d["gap_sec"], 1), 6)),
        })

    def _render_traffic_close(self, d: dict) -> Optional[str]:
        key = "traffic_close_ahead" if d["direction"] == "ahead" else "traffic_close_behind"
        return self.pool.pick(key, {})

    def _render_fuel_critical(self, d: dict) -> Optional[str]:
        return self.pool.pick("fuel_critical", {
            "fuel_laps": int(min(max(d["fuel_laps"], 1), 4)),
        })

    def _render_fuel_warning(self, d: dict) -> Optional[str]:
        return self.pool.pick("fuel_warning", {
            "fuel_laps": int(min(max(d["fuel_laps"], 1), 4)),
        })

    def _render_pit_call(self, d: dict) -> Optional[str]:
        return self.pool.pick("pit_call", {})

    def _render_damage(self, d: dict) -> Optional[str]:
        return self.pool.pick("damage", {})

    def _render_penalty(self, d: dict) -> Optional[str]:
        return self.pool.pick("penalty", {})

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


def iter_pregen_texts(pool: PhrasePool):
    """
    사전 캐시 대상 텍스트 전체를 생성 (tools/pregen_audio.py용).
    런타임 렌더러와 동일한 슬롯 조합을 열거해야 캐시가 히트한다.
    """
    slot_values = {
        "traffic_approach": [{"cls": c, "gap": g}
                             for c in ("하이퍼카", "엘엠피 투", "GT3", "상위 클래스")
                             for g in range(1, 7)],
        "traffic_close_ahead": [{}],
        "traffic_close_behind": [{}],
        "fuel_warning": [{"fuel_laps": n} for n in range(1, 5)],
        "fuel_critical": [{"fuel_laps": n} for n in range(1, 5)],
        "pit_call": [{}],
        "damage": [{}],
        "penalty": [{}],
    }
    for pool_key, combos in slot_values.items():
        for phrase in pool.pools.get(pool_key, []):
            for slots in combos:
                try:
                    yield phrase.format(**slots)
                except (KeyError, IndexError):
                    continue
