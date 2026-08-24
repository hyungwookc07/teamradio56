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
Output rules:
- Output exactly ONE line of team radio to the driver. No quotes, no preamble.
- ENGLISH ONLY. Real endurance-race engineer style: short, clipped, word-first.
  One or two short sentences. "Tyres near the cliff. Manage mode." — not prose.
- Judgment over numbers. At most one or two key figures.
- Combine data into a conclusion (fuel 10 laps + tyres 8 laps ->
  "Box lap nine, we fix both.").
- Never repeat recent radio lines. Keep the story flowing.
- Generation lags reality: never state live gaps as fact ("2.3 behind" is
  forbidden). Use general or predictive phrasing ("he keeps coming",
  "expect pressure into the next straight").
- Advise action, not just facts.
- If nothing is worth saying, output exactly PASS.
- Situation summaries below may be in Korean — still answer in English.
""".strip()

def _base_dir() -> str:
    # PyInstaller onefile 번들에서는 데이터 파일이 sys._MEIPASS에 풀린다
    import sys
    return getattr(sys, "_MEIPASS", None) or os.path.dirname(os.path.abspath(__file__))


def lines_file(lang: str = "en") -> str:
    """언어별 멘트 풀 파일 (en 기본, ko는 레거시 유지)."""
    name = "urgent_ko.yaml" if lang == "ko" else "urgent_en.yaml"
    return os.path.join(_base_dir(), "voice_lines", name)


URGENT_LINES_FILE = lines_file()

# 게임 클래스명 → 무전에서 부르는 이름 (영어)
CLASS_NAMES = {
    "Hypercar": "Hypercar",
    "LMH": "Hypercar",
    "LMDh": "Hypercar",
    "LMP2": "LMP2",
    "LMGT3": "GT3",
    "GT3": "GT3",
    "GTE": "GTE",
}


def class_name(cls: str) -> str:
    for key, name in CLASS_NAMES.items():
        if key.lower() in cls.lower():
            return name
    return "faster car"


class_ko = class_name    # 하위 호환 별칭


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
        self.budget_per_hour = cfg.get("llm.budget_per_hour", 15)
        self._call_times: list[float] = []
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

    def _budget_ok(self) -> bool:
        """호출 예산: 시간당 N회 (레이스 2시간 기준 10~30회 목표)."""
        import time as _time
        now = _time.monotonic()
        self._call_times = [t for t in self._call_times if now - t < 3600]
        if len(self._call_times) >= self.budget_per_hour:
            log.info("LLM 호출 예산 소진 (시간당 %d회) — 이번 멘트는 템플릿/침묵",
                     self.budget_per_hour)
            return False
        self._call_times.append(now)
        return True

    def generate(self, situation: str) -> Optional[str]:
        if self._disabled or not self._budget_ok():
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
            return ""       # LLM이 침묵을 선택 (발화 억제) — 오류(None)와 구분
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
    if state.issues:
        lines.append("[진행 중 이슈]")
        lines.extend(f"- {text}" for text in state.issues.values())

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

    if event.data.get("triggers"):
        lines.append("[전략 트리거]")
        lines.extend(f"- {t}" for t in event.data["triggers"])
    lines.append("[지금 말할 주제]")
    topic = {
        EventType.PACE_COMMENT: "방금 랩타임이 평소 대비 {delta:+.1f}초였다. 이유 추정이나 지시를 짧게.",
        EventType.GAP_COMMENT: "갭 변화: {who} 쪽이 랩당 {rate:+.1f}초씩 변하는 중 (현재 {gap:.0f}초). 판단을 말해라.",
        EventType.LAP_ANALYSIS: "연료 {fuel_l}L(랩당 {burn_per_lap}L, {fuel_laps}랩 분량), 잔여 레이스 {race_laps_left}랩. 타이어 상태와 엮어 피트 전략 판단을 말해라.",
        EventType.STINT_BRIEFING: "새 스틴트 시작. 이번 스틴트 목표를 한 줄로 브리핑해라.",
        EventType.TYRE_WARNING: "타이어 경고({kind}). 연료/잔여 레이스와 엮어 드라이버가 뭘 해야 할지 말해라.",
        EventType.RIVAL_PIT: "클래스 {rel} P{their_class_place} {driver}가 방금 피트에 들어갔다. 언더컷/오버컷 관점에서 우리 대응을 판단해라.",
        EventType.RIVAL_PACE: "라이벌 페이스 인텔: {mode} 상황, 랩당 {diff}초 차이, 약 {laps}랩. 추격/방어 지시를 짧게.",
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
        self.pool = PhrasePool(lines_file(cfg.get("voice.language", "en")))
        self.llm = CrewChiefLLM(cfg)

    NONURGENT = (EventType.PACE_COMMENT, EventType.GAP_COMMENT,
                 EventType.LAP_ANALYSIS, EventType.STINT_BRIEFING,
                 EventType.TYRE_WARNING, EventType.RIVAL_PIT,
                 EventType.RIVAL_PACE)

    def text_for(self, ev: Event) -> tuple[Optional[str], str]:
        """(멘트 텍스트, 소스) 반환. 텍스트 None이면 침묵. 소스는 발화 로그용."""
        if ev.message:
            source = "llm" if ev.type == EventType.BRIDGE_FOLLOWUP else "composed"
            return ev.message, source
        renderer = getattr(self, f"_render_{ev.type}", None)
        if renderer is None:
            log.debug("렌더러 없는 이벤트 무시: %s", ev.type)
            return None, "none"
        fallback = renderer(ev.data, ev.tone)
        if ev.type in self.NONURGENT:
            return self._llm_or(ev, fallback)   # LLM 우선 (3~5초 지연 허용)
        return fallback, "cache"                 # 긴급 콜은 변형 풀 즉시 반환

    # -- 긴급 콜: 사전 생성 변형 풀 ------------------------------------------
    # 슬롯 값은 반드시 이산화(정수/고정 문자열)한다. 사전 캐시 히트 조건.

    def _render_traffic_approach(self, d: dict, tone: str = "casual") -> Optional[str]:
        g = int(min(max(d["gap_sec"], 1), 6))
        return self.pool.pick("traffic_approach", {
            "cls": class_name(d["cls"]),
            "gap": f"{g} second" + ("s" if g > 1 else ""),
        }, tone)

    def _render_traffic_close(self, d: dict, tone: str = "casual") -> Optional[str]:
        # 트래픽 상태 머신이 풀 이름을 지정 (alongside/alongside_left/.../nearby_behind)
        return self.pool.pick(d["pool"], {"cls": class_name(d.get("cls", ""))}, tone)

    def _render_traffic_update(self, d: dict, tone: str = "casual") -> Optional[str]:
        # pass_complete / dropped — 서사 마무리 멘트
        return self.pool.pick(d["pool"], {"cls": class_name(d.get("cls", ""))}, tone)

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

    # 레이스 컨트롤 계열: 상태 머신이 data["pool"]로 풀 지정 (race_start/
    # fcy_start/fcy_pit_open/green_flag/pit_limiter)
    def _render_race_start(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick(d["pool"], {}, tone)

    _render_fcy = _render_race_start
    _render_fcy_pit_open = _render_race_start
    _render_green_flag = _render_race_start
    _render_pit_limiter = _render_race_start
    _render_blue_flag = _render_race_start

    def _render_spotter(self, d: dict, tone: str = "urgent") -> Optional[str]:
        # 스포터 콜: alongside_left/right/both(슬롯 없음), side_clear({side})
        return self.pool.pick(d["pool"], {"side": d.get("side", "")}, tone)

    def _render_race_end(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick("race_end", {
            "place": int(min(max(d.get("class_place", 1), 1), 8)),
        }, tone)

    def _render_position_change(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self.pool.pick(d["pool"], {
            "place": int(min(max(d.get("class_place", 1), 1), 8)),
        }, tone)

    def _render_race_milestone(self, d: dict, tone: str = "casual") -> Optional[str]:
        m = d.get("remaining_min")
        if m is None:
            return None
        if m >= 60:
            h = m // 60
            return f"{h} hour{'s' if h > 1 else ''} to go. On plan."
        return f"{m} minutes to go. Rechecking fuel and tyres."

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

    def _llm_or(self, ev: Event, fallback: Optional[str]) -> tuple[Optional[str], str]:
        if self.llm.available:
            text = self.llm.generate(build_situation(self.state, ev))
            if text:
                return text, "llm"
            if text == "":                  # LLM이 의도적으로 PASS → 침묵이 기본값
                return None, "llm-pass"
            # None = 오류/예산 소진 → 템플릿 폴백
        return fallback, "template"

    def _render_lap_analysis(self, d: dict, tone: str = "casual") -> Optional[str]:
        # 템플릿 폴백: 판단형 최소 멘트
        if d.get("pit_window_laps") is not None:
            return f"Pit window open. Box within {d['pit_window_laps']} laps."
        return None

    def _render_stint_briefing(self, d: dict, tone: str = "casual") -> Optional[str]:
        return "New stint. Easy on the tyres first lap, find the rhythm."

    def _render_rival_pit(self, d: dict, tone: str = "casual") -> Optional[str]:
        rel = "ahead" if d["rel"] == "앞" else "behind"
        base = f"P{d['their_class_place']} in class, car {rel}, just pitted."
        if d.get("undercut_risk"):
            return base + " Undercut attempt. I'll look at our timing."
        return base + " I'll call the gap when he's out."

    def _render_rival_pace(self, d: dict, tone: str = "casual") -> Optional[str]:
        if d["mode"] == "catch":
            return (f"Car ahead in class is {d['diff']:.1f} a lap slower. "
                    f"We catch him in {d['laps']}.")
        return (f"Car behind in class is {d['diff']:.1f} a lap quicker. "
                f"With us in {d['laps']} laps. Be ready.")

    def _render_tyre_warning(self, d: dict, tone: str = "casual") -> Optional[str]:
        if d.get("kind") == "temp_imbalance":
            return f"{d['hot_wheel']} tyre running {d['delta']:.0f} degrees hot. Ease that side."
        if d.get("kind") == "wear":
            return f"{d['wheel']} tyre, about {d['laps_left']:.0f} laps left. Factoring it in."
        return None

    def _render_pace_comment(self, d: dict, tone: str = "casual") -> Optional[str]:
        delta = abs(d["delta"])
        if d.get("direction") == "slower":
            return f"Lost {delta:.1f} on that lap. Checking why."
        return f"{delta:.1f} quicker. Keep the rhythm."

    def _render_gap_comment(self, d: dict, tone: str = "casual") -> Optional[str]:
        rate = d["rate"]
        gap = d["gap"]
        gap_s = f"{gap:.1f}" if gap < 10 else f"{gap:.0f}"
        if d["who"] == "behind":
            if rate <= -0.15:
                return f"Car behind closing {abs(rate):.1f} a lap. Gap {gap_s}. Just no mistakes."
            if rate >= 0.15:
                return f"Gap behind {gap_s} and opening. Good."
            return f"Gap behind {gap_s}, holding. Rhythm's good."
        if rate <= -0.15:
            return f"Gap ahead {gap_s}, closing {abs(rate):.1f} a lap. We can get him."
        if rate >= 0.15:
            return f"Gap ahead {gap_s} and opening. Don't overdo it."
        return f"Gap ahead {gap_s}, holding. Keep the rhythm."


def iter_pregen_texts(pool: PhrasePool):
    """
    사전 캐시 대상 텍스트 전체를 생성 (tools/pregen_audio.py용).
    런타임 렌더러와 동일한 슬롯 조합을 열거해야 캐시가 히트한다.
    """
    CLASSES = ("Hypercar", "LMP2", "GT3", "GTE", "faster car", "")
    slot_values = {
        "traffic_approach": [
            {"cls": c, "gap": f"{g} second" + ("s" if g > 1 else "")}
            for c in CLASSES if c for g in range(1, 7)],
        "alongside": [{}],
        "alongside_left": [{}],
        "alongside_right": [{}],
        "nearby_behind": [{"cls": c} for c in CLASSES],
        "pass_complete": [{"cls": c} for c in CLASSES],
        "dropped": [{"cls": c} for c in CLASSES],
        "backmarker_ahead": [{"cls": c} for c in CLASSES],
        "blue_flag": [{}],
        "alongside_both": [{}],
        "side_clear": [{"side": s} for s in ("left", "right")],
        "fuel_warning": [{"fuel_laps": n} for n in range(1, 5)],
        "fuel_critical": [{"fuel_laps": n} for n in range(1, 5)],
        "pit_call": [{}],
        "damage": [{}],
        "penalty": [{}],
        "race_start": [{}],
        "fcy_start": [{}],
        "fcy_pit_open": [{}],
        "green_flag": [{}],
        "pit_limiter": [{}],
        "race_end": [{"place": n} for n in range(1, 9)],
        "position_up": [{"place": n} for n in range(1, 9)],
        "position_down": [{"place": n} for n in range(1, 9)],
    }
    for pool_key, combos in slot_values.items():
        for tone in ("casual", "urgent"):
            for phrase in pool.lines(pool_key, tone):
                for slots in combos:
                    try:
                        # 톤을 함께 넘긴다 — TTS 캐시 키가 톤별 전달
                        # (속도/피치)을 포함하므로 같은 문장도 톤이 다르면
                        # 다른 오디오다.
                        yield tone, phrase.format(**slots)
                    except (KeyError, IndexError):
                        continue
