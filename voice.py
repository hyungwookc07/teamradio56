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
from typing import Callable, Optional

import yaml

from events import Event, EventType
from messages import (msg, class_display, gap_slot, side_slot,
                      fuel_slot, laps_text, seconds_text,
                      is_ko, set_language, and_list, join_problems,
                      multi_names, penalty_text, split_sentences,
                      wheel_name, zone_display, brief_length_text)
from speech_domain import pit_window_ok

log = logging.getLogger("voice")

# 페르소나 뒤에 붙는 고정 규칙. 페르소나+규칙은 바이트 단위로 불변이어야
# prompt cache가 유지된다 — 시간/랩 번호 같은 가변 값을 여기 넣지 말 것.
LLM_RULES_EN = """
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

LLM_RULES_KO = """
출력 규칙:
- 팀 무전으로 드라이버에게 하는 말 한 줄만 출력한다. 따옴표, 설명, 머리말 금지.
- 한국어 반말, 1~2문장, 짧게. 무전 특유의 간결한 호흡.
- 단어 위주로 끊어 말하고 어미를 아껴라. "타이어가 많이 닳았으니까 관리해야 해"
  대신 "타이어 한계 근접. 관리 모드." 실제 팀라디오 스타일.
- 숫자를 나열하지 말고 판단을 말한다. 꼭 필요한 숫자만 한두 개.
- 여러 데이터를 엮어서 결론을 내라. (예: 연료는 10랩인데 타이어가 8랩쯤 한계면
  "9랩째 들어와서 같이 해결하자"처럼)
- 지난 멘트와 같은 표현을 반복하지 마라. 서사가 이어지게.
- 생성 지연이 있으므로 실시간 수치를 단정하지 마라: "지금 2.3초 뒤" 금지,
  "계속 붙어오는 상황이야", "다음 스트레이트에서 압박 올 거야" 같은
  범용/예측형 표현을 써라.
- 사실 통보가 아니라 행동 조언 형태로. 말할 가치가 없으면 정확히 PASS 라고만 출력한다.
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
            "text": cfg.get("voice.persona", "") + "\n\n"
                    + (LLM_RULES_KO
                       if str(cfg.get("voice.language", "en")).startswith("ko")
                       else LLM_RULES_EN),
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
    }.get(event.type, "지금 상황에 대해 레이스 엔지니어로서 한마디 해라.")
    try:
        lines.append(topic.format(**event.data))
    except (KeyError, IndexError):
        lines.append(topic)
    return "\n".join(lines)


class VoiceGenerator:
    def __init__(self, cfg, state, speakable: Callable[[], bool] = lambda: True):
        self.cfg = cfg
        self.state = state
        self.pool = PhrasePool(lines_file(cfg.get("voice.language", "en")))
        self.llm = CrewChiefLLM(cfg)
        # 세션 목소리가 임의 문장을 말할 수 있는가 (tts.VoiceChain.free_text) —
        # 말할 수 없으면 LLM을 부르지 않고 템플릿 멘트를 쓴다 (API 호출도 없음)
        self._speakable = speakable

    @property
    def llm_speakable(self) -> bool:
        return self.llm.available and bool(self._speakable())

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

    @staticmethod
    def pool_call(ev_type: str, d: dict) -> Optional[tuple[str, dict]]:
        """변형 풀 콜의 (풀 키, 슬롯) — 클램프·이산화까지. 풀 콜이 아니거나 풀 지정이
        없으면 None. 렌더러와 커버리지 퍼즈가 같이 쓴다 (C# VoiceRenderer.PoolCall)."""
        T = EventType
        if ev_type == T.TRAFFIC_APPROACH:
            g = int(min(max(d["gap_sec"], 1), 6))
            return "traffic_approach", {"cls": class_display(d["cls"]), "gap": gap_slot(g)}
        if ev_type in (T.TRAFFIC_CLOSE, T.TRAFFIC_UPDATE):
            # 트래픽 상태 머신이 풀 이름을 지정 (alongside/.../nearby_behind, pass_complete/dropped)
            pool = d.get("pool")
            return (pool, {"cls": class_display(d.get("cls", ""))}) if pool else None
        if ev_type in (T.FUEL_CRITICAL, T.FUEL_WARNING):
            return ev_type, {"fuel_laps": fuel_slot(int(min(max(d["fuel_laps"], 1), 4)))}
        if ev_type in (T.PIT_CALL, T.DAMAGE, T.PENALTY):
            return ev_type, {}
        if ev_type in (T.RACE_START, T.FCY, T.FCY_PIT_OPEN, T.GREEN_FLAG,
                       T.PIT_LIMITER, T.BLUE_FLAG):
            # 레이스 컨트롤 계열: 상태 머신이 data["pool"]로 풀 지정
            pool = d.get("pool")
            return (pool, {}) if pool else None
        if ev_type == T.SPOTTER:
            # 스포터 콜: alongside_left/right/both(슬롯 없음), side_clear({side})
            pool = d.get("pool")
            return (pool, {"side": side_slot(d.get("side", ""))}) if pool else None
        if ev_type == T.RACE_END:
            return "race_end", {"place": int(min(max(d.get("class_place", 1), 1), 8))}
        if ev_type == T.POSITION_CHANGE:
            pool = d.get("pool")
            if not pool:
                return None
            return pool, {"place": int(min(max(d.get("class_place", 1), 1), 8))}
        return None

    def _render_pool(self, ev_type: str, d: dict, tone: str) -> Optional[str]:
        call = self.pool_call(ev_type, d)
        if call is None:
            return None
        return self.pool.pick(call[0], call[1], tone)

    def _render_traffic_approach(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.TRAFFIC_APPROACH, d, tone)

    def _render_traffic_close(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.TRAFFIC_CLOSE, d, tone)

    def _render_traffic_update(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.TRAFFIC_UPDATE, d, tone)

    def _render_fuel_critical(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.FUEL_CRITICAL, d, tone)

    def _render_fuel_warning(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.FUEL_WARNING, d, tone)

    def _render_pit_call(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.PIT_CALL, d, tone)

    def _render_race_start(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.RACE_START, d, tone)

    _render_fcy = _render_race_start
    _render_fcy_pit_open = _render_race_start
    _render_green_flag = _render_race_start
    _render_pit_limiter = _render_race_start
    _render_blue_flag = _render_race_start

    def _render_spotter(self, d: dict, tone: str = "urgent") -> Optional[str]:
        return self._render_pool(EventType.SPOTTER, d, tone)

    def _render_race_end(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.RACE_END, d, tone)

    def _render_position_change(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.POSITION_CHANGE, d, tone)

    def _render_race_milestone(self, d: dict, tone: str = "casual") -> Optional[str]:
        m = d.get("remaining_min")
        if m is None:
            return None
        if m >= 60:
            h = m // 60
            return msg("milestone_hours", h=h, s="s" if (h > 1 and not is_ko()) else "")
        return msg("milestone_minutes", m=m)

    def _render_damage(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.DAMAGE, d, tone)

    def _render_penalty(self, d: dict, tone: str = "casual") -> Optional[str]:
        return self._render_pool(EventType.PENALTY, d, tone)

    # -- 비긴급 멘트: LLM 우선, 실패 시 템플릿 폴백 ---------------------------

    def bridge_text(self, ev: Event) -> Optional[str]:
        """
        긴급 콜 직후 이어붙일 LLM 후속 멘트 (브리지 기법).
        긴급 콜이 이미 사실을 전달했으므로, 후속은 맥락/조언만 더한다.
        """
        if not self.llm_speakable or not ev.bridge:
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
        if self.llm_speakable:
            text = self.llm.generate(build_situation(self.state, ev))
            if text:
                return text, "llm"
            if text == "":                  # LLM이 의도적으로 PASS → 침묵이 기본값
                return None, "llm-pass"
            # None = 오류/예산 소진 → 템플릿 폴백
        return fallback, "template"

    def _render_lap_analysis(self, d: dict, tone: str = "casual") -> Optional[str]:
        # 템플릿 폴백: 판단형 최소 멘트. 100랩 이상 창은 무의미한 값 — 침묵
        n = d.get("pit_window_laps")
        if n is not None and pit_window_ok(n):
            return msg("pit_window", n=laps_text(n))
        return None

    def _render_stint_briefing(self, d: dict, tone: str = "casual") -> Optional[str]:
        return msg("stint_brief")

    def _render_rival_pit(self, d: dict, tone: str = "casual") -> Optional[str]:
        rel = msg("rival_rel_ahead") if d["rel"] == "앞" else msg("rival_rel_behind")
        base = msg("rival_pit_base", rel=rel, p=d["their_class_place"])
        if d.get("undercut_risk"):
            return base + msg("rival_pit_undercut")
        return base + msg("rival_pit_gap")

    def _render_rival_pace(self, d: dict, tone: str = "casual") -> Optional[str]:
        if d["mode"] == "catch":
            return msg("rival_catch", diff=d["diff"], laps=laps_text(d["laps"]))
        return msg("rival_defend", diff=d["diff"], laps=laps_text(d["laps"]))

    def _render_tyre_warning(self, d: dict, tone: str = "casual") -> Optional[str]:
        from messages import wheel_display
        if d.get("kind") == "temp_imbalance":
            return msg("tyre_hot", wheel=wheel_display(d["hot_wheel"]),
                       delta=d["delta"])
        if d.get("kind") == "wear":
            return msg("tyre_wear", wheel=wheel_display(d["wheel"]),
                       laps=laps_text(d["laps_left"]))
        return None

    def _render_pace_comment(self, d: dict, tone: str = "casual") -> Optional[str]:
        delta = abs(d["delta"])
        if d.get("direction") == "slower":
            return msg("pace_lost", delta=delta)
        return msg("pace_quick", delta=delta)

    def _render_gap_comment(self, d: dict, tone: str = "casual") -> Optional[str]:
        rate = d["rate"]
        gap = d["gap"]
        gap_s = seconds_text(gap)   # 단위 포함 ("2.4 seconds"/"2.4초")
        if d["who"] == "behind":
            if rate <= -0.15:
                return msg("gap_behind_closing", rate=abs(rate), gap=gap_s)
            if rate >= 0.15:
                return msg("gap_behind_opening", gap=gap_s)
            return msg("gap_behind_holding", gap=gap_s)
        if rate <= -0.15:
            return msg("gap_ahead_closing", rate=abs(rate), gap=gap_s)
        if rate >= 0.15:
            return msg("gap_ahead_opening", gap=gap_s)
        return msg("gap_ahead_holding", gap=gap_s)


# 변형 풀(랜덤)을 안 쓰는 렌더러 — 같은 data면 항상 같은 문장.
# 리플레이 대조('say')가 C# VoiceRenderer.RenderDeterministic과 같은 집합을 쓴다.
DETERMINISTIC_TYPES = (
    EventType.GAP_COMMENT, EventType.PACE_COMMENT, EventType.LAP_ANALYSIS,
    EventType.STINT_BRIEFING, EventType.RIVAL_PIT, EventType.RIVAL_PACE,
    EventType.TYRE_WARNING, EventType.RACE_MILESTONE,
)


def render_deterministic(ev_type: str, data: dict,
                         tone: str = "casual") -> Optional[str]:
    """LLM 없이 나갈 템플릿 문장. 대상 타입이 아니면 None.
    이 렌더러들은 self(풀/LLM)를 건드리지 않으므로 인스턴스 없이 호출한다."""
    if ev_type not in DETERMINISTIC_TYPES:
        return None
    return getattr(VoiceGenerator, f"_render_{ev_type}")(None, data, tone)


def iter_pregen_texts(pool: PhrasePool):
    """
    사전 캐시 대상 텍스트 전체를 생성 (tools/pregen_audio.py용).
    런타임 렌더러와 동일한 슬롯 조합을 열거해야 캐시가 히트한다.
    """
    if is_ko():
        CLASSES = ("하이퍼카", "엘엠피 투", "GT3", "GTE", "상위 클래스", "")
        SIDES = ("왼쪽", "오른쪽")
    else:
        CLASSES = ("Hypercar", "LMP2", "GT3", "GTE", "faster car", "")
        SIDES = ("left", "right")
    slot_values = {
        "traffic_approach": [
            {"cls": c, "gap": gap_slot(g)}
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
        "side_clear": [{"side": s} for s in SIDES],
        "fuel_warning": [{"fuel_laps": fuel_slot(n)} for n in range(1, 5)],
        "fuel_critical": [{"fuel_laps": fuel_slot(n)} for n in range(1, 5)],
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
    if not is_ko():
        yield from iter_composed_whole_texts()


# 페널티 콜 열거 — analyzers.racecontrol._parse_penalty가 내는 값 전체
# (reason ""은 사유 미상). C# PregenTexts.PenaltyKinds/Reasons와 일치.
PEN_KINDS = ("drive-through", "stop-and-go", "time penalty", "penalty")
PEN_REASONS = ("", "pit lane speeding", "track limits",
               "yellow flag infringement", "start infringement",
               "contact", "blocking", "unsafe rejoin")


def iter_composed_whole_texts():
    """
    분석기가 조립하는(ev.message) 문장 중 경우의 수가 작은 것 — 통째로
    사전 생성한다 (en 전용, ko는 아무것도 내지 않는다). C# PregenTexts.ComposedWhole 미러.
    """
    if is_ko():
        return
    from analyzers.racecontrol import MILESTONES_MIN
    for n in range(0, 41):
        yield "casual", msg("pit_window", n=laps_text(n))
    for rel in ("ahead", "behind"):
        # rivals.py: undercut_risk == (뒤차가 먼저 피트)
        tail = msg("rival_pit_undercut") if rel == "behind" else msg("rival_pit_gap")
        for p in range(1, 31):
            yield "casual", msg("rival_pit_base", rel=msg("rival_rel_" + rel), p=p) + tail
    for m in MILESTONES_MIN:
        yield "casual", render_deterministic("race_milestone", {"remaining_min": m})
    for key in ("stint_brief", "final_lap", "pen_clear", "dmg_no_effect",
                "report_marks", "report_clean", "align_mild"):
        yield "casual", msg(key)
    for i in range(4):
        yield "casual", msg("slow_puncture", wheel=wheel_name(i))
        yield "casual", msg("puncture_now", wheel=wheel_name(i))
    for what in ("engine_what_water", "engine_what_oil", "engine_what_engine"):
        yield "casual", msg("engine_warn", what=msg(what))

    for n in range(1, 4):
        yield "urgent", msg("sector_yellow", n=n)
    yield "urgent", msg("stopped_hazard")
    for i in range(4):
        yield "urgent", msg("wheel_gone", wheel=wheel_name(i))
    for key in ("part_detached_rear", "part_detached", "align_severe", "rear_instab"):
        yield "urgent", msg(key)
    for kind in PEN_KINDS:
        for reason in PEN_REASONS:
            yield "urgent", penalty_text(kind, reason)


# -- 문장 단위 사전 생성 도메인 — speech_domain이 단일 출처 (옛 이름은 별칭) ------
from speech_domain import (SEG_GAPS, SEG_RATES, SEG_PACE,  # noqa: E402,F401
                           SEG_RIVAL_DIFF, SEG_RIVAL_LAPS, SEG_TYRE_HOT,
                           SEG_TYRE_LAPS, SEG_MULTI_CLASSES, SEG_MULTI_MAX_CARS,
                           SEG_DENT_MAX_ZONES, SEG_WING_MM, SEG_BRAKE_T,
                           SEG_REPAIR, SEG_FUEL_TARGET, SEG_FUEL_SAVE,
                           SEG_BRIEF_MINUTES, SEG_BRIEF_LAPS, SEG_BRIEF_CARS,
                           SEG_BRIEF_POS, SEG_BRIEF_TEMP, SEG_BRIEF_FUEL,
                           SEG_PIT_WINDOW, SEG_RIVAL_PIT, SEG_COACH,
                           SEG_LAPTIME_T10, SEG_STATUS_POS, SEG_STATUS_FUEL,
                           SEG_STATUS_TYRES, COACH_SECTORS, load_track_names)

# 음성 팩에 들어가는 문장 단계 (pregen/pack/CountMissing 기본값). C# PregenSentences.PackMaxTier
PACK_TIERS = (1, 2, 3)


def iter_pregen_sentences(tiers=PACK_TIERS, tracks=()):
    """
    문장 단위 사전 생성 대상 (톤, 문장) — 실제 멘트를 msg()/헬퍼로 슬롯
    도메인 전체에 대해 렌더한 뒤 split_sentences로 쪼갠다 (중복 제거).
    en 전용. tracks는 2단계(세션 브리핑)용. 3단계는 HUD 대체 리포트(랩타임·상황).
    """
    if is_ko():
        return
    seen: set = set()

    def emit(tone: str, text: str):
        for s in split_sentences(text):
            if (tone, s) not in seen:
                seen.add((tone, s))
                yield tone, s

    if 1 in tiers:
        for tone, text in _tier1_messages():
            yield from emit(tone, text)
    if 2 in tiers:
        for tone, text in _tier2_messages(tracks):
            yield from emit(tone, text)
    if 3 in tiers:
        for tone, text in _tier3_messages():
            yield from emit(tone, text)


def _tier1_messages():
    """1단계 도메인의 전체 멘트 (분할 전)."""
    from itertools import combinations, permutations, product
    from analyzers.health import DENT_ZONES

    # 갭 — VoiceGenerator._render_gap_comment
    for g in SEG_GAPS:
        gs = seconds_text(g)
        for who in ("behind", "ahead"):
            yield "casual", msg(f"gap_{who}_opening", gap=gs)
            yield "casual", msg(f"gap_{who}_holding", gap=gs)
            for r in SEG_RATES:
                yield "casual", msg(f"gap_{who}_closing", rate=r, gap=gs)
    # 페이스 — _render_pace_comment
    for d in SEG_PACE:
        yield "casual", msg("pace_lost", delta=d)
        yield "casual", msg("pace_quick", delta=d)
    # 라이벌 페이스 — _render_rival_pace
    for d in SEG_RIVAL_DIFF:
        for n in SEG_RIVAL_LAPS:
            yield "casual", msg("rival_catch", diff=d, laps=laps_text(n))
            yield "casual", msg("rival_defend", diff=d, laps=laps_text(n))
    # 타이어 — _render_tyre_warning
    for i in range(4):
        for d in SEG_TYRE_HOT:
            yield "casual", msg("tyre_hot", wheel=wheel_name(i), delta=d)
        for n in SEG_TYRE_LAPS:
            yield "casual", msg("tyre_wear", wheel=wheel_name(i), laps=laps_text(n))
    # 데미지 리포트 — health._damage_report (문제 하나 = 한 문장)
    wheels = [wheel_name(i) for i in range(4)]
    problems = []
    for k in range(1, 5):
        for sub in combinations(wheels, k):
            problems.append(msg("item_wheel_damage", names=and_list(list(sub))))
            problems.append(msg("item_puncture", names=and_list(list(sub))))
    for w in wheels:
        problems.append(msg("item_losing_pressure", name=w))
    zones = [zone_display(z) for z in DENT_ZONES]
    for k in range(1, SEG_DENT_MAX_ZONES + 1):
        for sub in combinations(zones, k):
            problems.append(msg("item_heavy_body", zones=and_list(list(sub))))
    problems.append(msg("item_heavy_body_all"))
    for p in problems:
        for adv in ("report_advice_box", "report_advice_keep"):
            yield "casual", msg("report_problems", problems=join_problems([p]),
                                advice=msg(adv))
    # 다중 차량 — traffic._compose_multi (절 하나 = 한 문장)
    for c in SEG_MULTI_CLASSES:
        for key in ("multi_alongside_left", "multi_alongside_right", "multi_alongside"):
            yield "urgent", msg(key, cls=c) + "."
    yield "urgent", msg("multi_ahead_clear")
    for k in range(1, SEG_MULTI_MAX_CARS + 1):
        for classes in permutations(SEG_MULTI_CLASSES, k):
            for counts in product(range(1, SEG_MULTI_MAX_CARS + 1), repeat=k):
                if sum(counts) > SEG_MULTI_MAX_CARS:
                    continue
                names = multi_names(list(zip(classes, counts)))
                yield "urgent", msg("multi_behind", names=names) + "."
                yield "urgent", msg("multi_closing", names=names) + "."
    yield "urgent", msg("multi_behind_many") + "."
    yield "urgent", msg("multi_closing_many") + "."
    # 피트 윈도우 41~99랩 / 라이벌 피트 P31~60 — 통문장 범위 밖은 문장 조립
    for n in SEG_PIT_WINDOW:
        yield "casual", msg("pit_window", n=laps_text(n))
    for rel in ("ahead", "behind"):
        tail = msg("rival_pit_undercut") if rel == "behind" else msg("rival_pit_gap")
        for p in SEG_RIVAL_PIT:
            yield "casual", msg("rival_pit_base", rel=msg("rival_rel_" + rel), p=p) + tail


def _tier2_messages(tracks=()):
    """2단계 도메인의 전체 멘트 (분할 전)."""
    # 차량 상태 — health (윙/브레이크/수리 판단)
    for mm in SEG_WING_MM:
        yield "casual", msg("wing_down", mm=mm)
    for t in SEG_BRAKE_T:
        yield "casual", msg("brake_warn", t=t)
    for d in SEG_REPAIR:
        yield "casual", msg("repair_cost", delta=d)
    yield "casual", msg("repair_cost_big")
    # 연료 세이브 — fuel._save_coaching
    for target in SEG_FUEL_TARGET:
        for d in SEG_FUEL_SAVE:
            yield "casual", msg("fuel_save", target=target, delta=d)
    # 세션 브리핑 — briefing.compose_session_briefing (트랙 이름은 목록 파일)
    for kind_key in ("kind_race", "kind_warmup", "kind_quali", "kind_practice"):
        kind = msg(kind_key)
        yield "casual", msg("brief_radio", kind=kind)
        for track in tracks:
            yield "casual", msg("brief_track", track=track, kind=kind)
    for m in SEG_BRIEF_MINUTES:
        length = brief_length_text(m)
        yield "casual", msg("brief_len_remaining", length=length)
        yield "casual", msg("brief_len_long", length=length)
    for n in SEG_BRIEF_LAPS:
        yield "casual", msg("brief_lap") if n == 1 else msg("brief_laps", n=n)
    for n in SEG_BRIEF_CARS:
        for cp in SEG_BRIEF_POS:
            yield "casual", msg("brief_grid", cp=cp, n=n)
            yield "casual", msg("brief_grid_mid", cp=cp, n=n)
    for t in SEG_BRIEF_TEMP:
        yield "casual", msg("brief_temp", t=t)
    for f in SEG_BRIEF_FUEL:
        yield "casual", msg("brief_fuel", f=f)
    for key in ("brief_rain", "brief_formation", "brief_start_spotter",
                "brief_carry_on", "brief_calm", "brief_out"):
        yield "casual", msg(key)
    # 트레이닝 섹터 피드백 — training.LapCoach.feedback_text
    for n in COACH_SECTORS:
        for d in SEG_COACH:
            yield "casual", msg("coach_quicker_best", n=n, d=d)
            yield "casual", msg("coach_quicker", n=n, d=d)
            yield "casual", msg("coach_losing", n=n, d=d)


def _tier3_messages():
    """3단계 — HUD 대체 리포트 (analyzers.reporter, 선택 기능)."""
    from messages import fmt_laptime
    for t10 in SEG_LAPTIME_T10:
        yield "casual", msg("last_lap_report", t=fmt_laptime(t10 / 10)) + msg("best_lap_suffix")
    for p in SEG_STATUS_POS:
        yield "casual", msg("status_pos", p=p)
    for n in SEG_STATUS_FUEL:
        yield "casual", msg("status_fuel", n=laps_text(n))
    for n in SEG_STATUS_TYRES:
        yield "casual", msg("status_tyres", n=laps_text(n))
    yield "casual", msg("status_tyres_good")
