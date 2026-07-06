"""
멘트 생성 — 이원화 설계.

  1) 긴급 콜: voice_lines/urgent_ko.yaml의 사전 생성 변형 풀에서 선택.
     랜덤 + 최근 사용 이력 제외로 반복감 방지. tools/pregen_audio.py로
     오디오까지 사전 캐시하면 지연 0.
  2) 비긴급 멘트: 실시간 LLM (Anthropic API, claude-haiku 계열).
     원시 텔레메트리가 아니라 파이썬에서 가공한 요약 상태를 입력으로 주고,
     여러 데이터를 엮은 판단형 멘트를 받는다. LLM 불가 시 템플릿 폴백.

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

# 페르소나 뒤에 붙는 고정 규칙. 페르소나+규칙은 바이트 단위로 불변이어야
# prompt cache가 유지된다 — 시간/랩 번호 같은 가변 값을 여기 넣지 말 것.
LLM_RULES = """
출력 규칙:
- 팀 무전으로 드라이버에게 하는 말 한 줄만 출력한다. 따옴표, 설명, 머리말 금지.
- 한국어 반말, 1~2문장, 짧게. 무전 특유의 간결한 호흡.
- 숫자를 나열하지 말고 판단을 말한다. 꼭 필요한 숫자만 한두 개.
- 여러 데이터를 엮어서 결론을 내라. (예: 연료는 10랩인데 타이어가 8랩쯤 한계면
  "9랩째 들어와서 같이 해결하자"처럼)
- 지난 멘트와 같은 표현을 반복하지 마라. 서사가 이어지게.
- 생성 지연이 있으므로 실시간 수치를 단정하지 마라: "지금 2.3초 뒤" 금지,
  "계속 붙어오는 상황이야", "다음 스트레이트에서 압박 올 거야" 같은
  범용/예측형 표현을 써라.
- 사실 통보가 아니라 행동 조언 형태로: "왼쪽 GT3 신경 쓰고 다음 코너 라인 깔끔하게".
- 말할 가치가 없으면 정확히 PASS 라고만 출력한다.
""".strip()

def _base_dir() -> str:
    # PyInstaller onefile 번들에서는 데이터 파일이 sys._MEIPASS에 풀린다
    import sys
    return getattr(sys, "_MEIPASS", None) or os.path.dirname(os.path.abspath(__file__))


URGENT_LINES_FILE = os.path.join(_base_dir(), "voice_lines", "urgent_ko.yaml")

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
    """
    이벤트 타입(풀 키) × 톤(casual/urgent)별 변형 멘트 풀.
    최근 사용 이력(키 단위 큐)을 피해서 뽑아 반복감을 줄인다.
    """

    RECENT_EXCLUDE = 5   # 같은 풀에서 최근 N개는 다시 안 씀

    def __init__(self, path: str = URGENT_LINES_FILE):
        self.pools: dict = {}
        self._recent: dict[str, deque] = {}
        try:
            with open(path, "r", encoding="utf-8") as f:
                self.pools = yaml.safe_load(f) or {}
            total = sum(len(lines) for tones in self.pools.values()
                        for lines in (tones.values() if isinstance(tones, dict) else [tones]))
            log.info("멘트 풀 로드: %d개 타입, 총 %d개 변형", len(self.pools), total)
        except OSError as e:
            log.warning("멘트 풀 로드 실패(%s) — 템플릿 폴백", e)

    def lines(self, pool_key: str, tone: str = "casual") -> list[str]:
        entry = self.pools.get(pool_key)
        if entry is None:
            return []
        if isinstance(entry, list):           # 톤 구분 없는 구형 포맷 호환
            return entry
        lines = entry.get(tone)
        if not lines:                          # 해당 톤이 없으면 있는 톤으로 폴백
            for alt in entry.values():
                if alt:
                    return alt
            return []
        return lines

    def pick(self, pool_key: str, slots: dict, tone: str = "casual") -> Optional[str]:
        pool = self.lines(pool_key, tone)
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


class CrewChiefLLM:
    """
    비긴급 멘트 실시간 생성 (Anthropic API).

    - 페르소나+규칙은 system 블록에 cache_control을 걸어 prompt caching 활용.
      (가변 내용은 절대 system에 넣지 않는다 — 캐시 프리픽스가 깨진다.)
    - 레이스 상황 요약/서사는 매번 바뀌므로 user 턴으로 전달.
    - 어떤 실패(키 없음, 타임아웃, 레이트리밋)에도 None을 반환하고,
      호출부가 템플릿으로 폴백한다. 앱은 절대 죽지 않는다.
    """

    def __init__(self, cfg):
        self.model = cfg.get("llm.model", "claude-haiku-4-5")
        self.max_tokens = cfg.get("llm.max_tokens", 200)
        self.timeout = cfg.get("llm.timeout_sec", 10)
        self._api_key = cfg.anthropic_api_key
        self._system = [{
            "type": "text",
            "text": cfg.get("voice.persona", "") + "\n\n" + LLM_RULES,
            "cache_control": {"type": "ephemeral"},
        }]
        self._client = None
        self._disabled = not (cfg.get("llm.enabled", True) and self._api_key)
        if self._disabled:
            log.info("LLM 비활성 (enabled=false 또는 API 키 없음) — 템플릿 멘트 사용")

    @property
    def available(self) -> bool:
        return not self._disabled

    def _get_client(self):
        if self._client is None:
            import anthropic
            self._client = anthropic.Anthropic(
                api_key=self._api_key,
                timeout=float(self.timeout),
                max_retries=1,      # 멘트는 실시간성이 생명 — 오래 재시도하지 않는다
            )
        return self._client

    def generate(self, situation: str) -> Optional[str]:
        if self._disabled:
            return None
        try:
            import anthropic
        except ImportError:
            log.warning("anthropic 패키지 미설치 — 템플릿 폴백 (pip install anthropic)")
            self._disabled = True
            return None
        try:
            response = self._get_client().messages.create(
                model=self.model,
                max_tokens=self.max_tokens,
                system=self._system,
                messages=[{"role": "user", "content": situation}],
            )
        except anthropic.AuthenticationError:
            log.error("Anthropic API 키 인증 실패 — LLM 비활성화, 템플릿 폴백")
            self._disabled = True
            return None
        except anthropic.RateLimitError:
            log.warning("API 레이트리밋 — 이번 멘트는 템플릿 폴백")
            return None
        except anthropic.APIStatusError as e:
            log.warning("API 오류(%s) — 템플릿 폴백", e.status_code)
            return None
        except (anthropic.APIConnectionError, anthropic.APITimeoutError) as e:
            log.warning("API 연결/타임아웃 — 템플릿 폴백: %s", e)
            return None

        text = "".join(b.text for b in response.content if b.type == "text").strip()
        text = text.strip('"“” ').strip()
        if not text or text.upper() == "PASS":
            return None     # LLM이 침묵을 선택 (발화 억제)
        # 멘트가 비정상적으로 길면 첫 두 문장만
        if len(text) > 160:
            parts = [p for p in text.replace("\n", " ").split(". ") if p]
            text = ". ".join(parts[:2]).strip()
        return text


def build_situation(state, event: Event) -> str:
    """
    LLM 입력용 요약 상태. 원시 텔레메트리가 아니라 가공된 값만 담는다.
    매 호출 내용이 달라지므로 user 턴으로 보낸다 (system에 넣으면 캐시 무효화).
    """
    lines = ["[레이스 상황]"]
    lines.append(f"트랙 {state.track}, 클래스 {state.player_class} (동클래스 {state.class_vehicles}대)")

    valid = [l for l in state.laps if l.valid]
    if valid:
        last = state.laps[-1]
        recent = ", ".join(f"{l.lap_time:.1f}s" for l in valid[-4:])
        lines.append(f"랩 {last.lap_number} 완료, P{last.place}. 최근 랩타임: {recent}")
        if last.gap_ahead >= 0:
            lines.append(f"앞차 갭 {last.gap_ahead:.1f}초")
        if last.gap_behind >= 0:
            lines.append(f"뒤차 갭 {last.gap_behind:.1f}초")
        if last.fuel_left >= 0:
            lines.append(f"연료 {last.fuel_left:.1f}L")
        # 타이어 추세: 최근 3랩 마모율 → 남은 수명 추정 (마모 한계 0.25 기준)
        if len(valid) >= 3 and valid[-1].tyre_wear and valid[-3].tyre_wear:
            worst = None
            names = ["좌앞", "우앞", "좌뒤", "우뒤"]
            for i in range(min(len(valid[-1].tyre_wear), 4)):
                rate = (valid[-3].tyre_wear[i] - valid[-1].tyre_wear[i]) / 2
                if rate > 1e-4:
                    laps_left = (valid[-1].tyre_wear[i] - 0.25) / rate
                    if worst is None or laps_left < worst[1]:
                        worst = (names[i], laps_left, valid[-1].tyre_wear[i])
            if worst is not None:
                lines.append(
                    f"타이어: {worst[0]}이 가장 나쁨, 남은 수명 {worst[1]:.0f}랩 추정 "
                    f"(잔량 {worst[2]*100:.0f}%)")

    if state.narrative:
        lines.append("[최근 무전 내역 — 같은 말 반복 금지]")
        lines.extend(state.narrative[-5:])

    lines.append("[지금 말할 주제]")
    topic = {
        EventType.PACE_COMMENT: "방금 랩타임이 평소 대비 {delta:+.1f}초였다. 이유 추정이나 지시를 짧게.",
        EventType.GAP_COMMENT: "갭 변화: {who} 쪽이 랩당 {rate:+.1f}초씩 변하는 중 (현재 {gap:.0f}초). 판단을 말해라.",
        EventType.LAP_ANALYSIS: "연료 {fuel_l}L(랩당 {burn_per_lap}L, {fuel_laps}랩 분량), 잔여 레이스 {race_laps_left}랩. 타이어 상태와 엮어 피트 전략 판단을 말해라.",
        EventType.STINT_BRIEFING: "새 스틴트 시작. 이번 스틴트 목표를 한 줄로 브리핑해라.",
        EventType.TYRE_WARNING: "타이어 경고({kind}). 연료/잔여 레이스와 엮어 드라이버가 뭘 해야 할지 말해라.",
    }.get(event.type, "지금 상황에 대해 크루치프로서 한마디 해라.")
    try:
        lines.append(topic.format(**event.data))
    except (KeyError, IndexError):
        lines.append(topic)
    return "\n".join(lines)


class VoiceGenerator:
    def __init__(self, cfg, state):
        self.cfg = cfg
        self.state = state
        self.pool = PhrasePool()
        self.llm = CrewChiefLLM(cfg)

    NONURGENT = (EventType.PACE_COMMENT, EventType.GAP_COMMENT,
                 EventType.LAP_ANALYSIS, EventType.STINT_BRIEFING,
                 EventType.TYRE_WARNING)

    def text_for(self, ev: Event) -> Optional[str]:
        if ev.message:
            return ev.message
        renderer = getattr(self, f"_render_{ev.type}", None)
        if renderer is None:
            log.debug("렌더러 없는 이벤트 무시: %s", ev.type)
            return None
        fallback = renderer(ev.data, ev.tone)
        if ev.type in self.NONURGENT:
            return self._llm_or(ev, fallback)   # LLM 우선 (3~5초 지연 허용)
        return fallback                          # 긴급 콜은 변형 풀 즉시 반환

    # -- 긴급 콜: 사전 생성 변형 풀 ------------------------------------------
    # 슬롯 값은 반드시 이산화(정수/고정 문자열)한다. 사전 캐시 히트 조건.

    def _render_traffic_approach(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("traffic_approach", {
            "cls": class_ko(d["cls"]),
            "gap": int(min(max(d["gap_sec"], 1), 6)),
        }, tone)

    def _render_traffic_close(self, d: dict, tone: str = "casual") -> Optional[str]:
        # 트래픽 상태 머신이 풀 이름을 지정 (alongside/alongside_left/.../nearby_behind)
        return self.pool.pick(d["pool"], {"cls": class_ko(d.get("cls", ""))}, tone)

    def _render_traffic_update(self, d: dict, tone: str = "casual") -> Optional[str]:
        # pass_complete / dropped — 서사 마무리 멘트
        return self.pool.pick(d["pool"], {"cls": class_ko(d.get("cls", ""))}, tone)

    def _render_fuel_critical(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("fuel_critical", {
            "fuel_laps": int(min(max(d["fuel_laps"], 1), 4)),
        }, tone)

    def _render_fuel_warning(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("fuel_warning", {
            "fuel_laps": int(min(max(d["fuel_laps"], 1), 4)),
        }, tone)

    def _render_pit_call(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("pit_call", {}, tone)

    def _render_damage(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("damage", {}, tone)

    def _render_penalty(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("penalty", {}, tone)

    # -- 비긴급 멘트: LLM 우선, 실패 시 템플릿 폴백 ---------------------------

    def bridge_text(self, ev: Event) -> Optional[str]:
        """
        긴급 콜 직후 이어붙일 LLM 후속 멘트 (브리지 기법).
        긴급 콜이 이미 사실을 전달했으므로, 후속은 맥락/조언만 더한다.
        """
        if not self.llm.available or not ev.bridge:
            return None
        lines = ["[레이스 상황]"]
        if self.state.narrative:
            lines.append("[최근 무전 내역 — 같은 말 반복 금지]")
            lines.extend(self.state.narrative[-4:])
        lines.append("[방금 나간 긴급 콜의 상황]")
        lines.append(ev.bridge.get("topic", ""))
        lines.append(
            "[지금 말할 주제] 방금 긴급 콜의 후속 설명을 한 문장으로. "
            "이미 전달된 사실 반복 금지, 드라이버가 어떻게 대응하면 되는지만.")
        return self.llm.generate("\n".join(lines))

    def _llm_or(self, ev: Event, fallback: Optional[str]) -> Optional[str]:
        if self.llm.available:
            text = self.llm.generate(build_situation(self.state, ev))
            if text is not None:
                return text
            # LLM이 PASS(침묵) 또는 오류 → 템플릿 폴백 (None이면 발화 안 함)
        return fallback

    def _render_lap_analysis(self, d: dict, tone: str = "casual") -> Optional[str]:
        # 템플릿 폴백: 판단형 최소 멘트
        if d.get("pit_window_laps") is not None:
            return f"피트 윈도우 계산 중이야. 늦어도 {d['pit_window_laps']}랩 안엔 들어와야 해."
        return None

    def _render_stint_briefing(self, d: dict, tone: str = "casual") -> Optional[str]:
        return "새 스틴트야. 첫 랩은 타이어 아끼고, 리듬부터 찾자."

    def _render_tyre_warning(self, d: dict, tone: str = "casual") -> Optional[str]:
        if d.get("kind") == "temp_imbalance":
            return f"{d['hot_wheel']} 타이어가 {d['delta']:.0f}도 더 뜨거워. 그쪽 코너 조금만 아껴줘."
        if d.get("kind") == "wear":
            return f"{d['wheel']} 타이어 수명이 {d['laps_left']:.0f}랩쯤 남았어. 피트 계획에 반영할게."
        return None

    def _render_pace_comment(self, d: dict, tone: str = "casual") -> Optional[str]:
        delta = abs(d["delta"])
        if d.get("direction") == "slower":
            return f"방금 랩, 평소보다 {delta:.1f}초 느렸어. 어디서 잃었는지 확인해봐."
        return f"좋아, 평소보다 {delta:.1f}초 빨라. 이 리듬 유지하자."

    def _render_gap_comment(self, d: dict, tone: str = "casual") -> Optional[str]:
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
    CLASSES = ("하이퍼카", "엘엠피 투", "GT3", "GTE", "상위 클래스", "")
    slot_values = {
        "traffic_approach": [{"cls": c, "gap": g} for c in CLASSES if c
                             for g in range(1, 7)],
        "alongside": [{}],
        "alongside_left": [{}],
        "alongside_right": [{}],
        "nearby_behind": [{"cls": c} for c in CLASSES],
        "pass_complete": [{"cls": c} for c in CLASSES],
        "dropped": [{"cls": c} for c in CLASSES],
        "fuel_warning": [{"fuel_laps": n} for n in range(1, 5)],
        "fuel_critical": [{"fuel_laps": n} for n in range(1, 5)],
        "pit_call": [{}],
        "damage": [{}],
        "penalty": [{}],
    }
    for pool_key, combos in slot_values.items():
        for tone in ("casual", "urgent"):
            for phrase in pool.lines(pool_key, tone):
                for slots in combos:
                    try:
                        yield phrase.format(**slots)
                    except (KeyError, IndexError):
                        continue
