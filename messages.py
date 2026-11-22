"""
발화 문장 언어 테이블 (en/ko) — 멘트 풀(voice_lines/*.yaml) 밖에서
분석기/메인 루프가 직접 조립하는 모든 문장이 여기를 거친다.

사용법:
    from messages import msg, set_language, wheel_name, class_display, ...
    set_language(cfg.get("voice.language", "en"))   # 시작 시 한 번
    message = msg("stopped_hazard")
    message = msg("wing_down", mm=drop * 1000)

규칙:
  - 키가 ko 테이블에 없으면 en으로 폴백한다 (누락돼도 침묵하지 않게).
  - 포맷 지시자({x:.1f} 등)는 두 언어가 같은 자리표를 쓰도록 유지한다.
  - 리플레이 회귀(tools/replay_calls.py)는 언어 기본값 en으로 돌므로
    영어 문장을 바꾸면 C# 포팅(Messages.cs)도 함께 바꿔야 한다.
"""

from __future__ import annotations

import re

LANG = "en"


def set_language(lang: str) -> None:
    global LANG
    LANG = "ko" if str(lang or "").lower().startswith("ko") else "en"


def is_ko() -> bool:
    return LANG == "ko"


def msg(key: str, **kw) -> str:
    table = _T.get(LANG) or _T["en"]
    tpl = table.get(key)
    if tpl is None:
        tpl = _T["en"][key]
    return tpl.format(**kw) if kw else tpl


# -- 슬롯/이름 헬퍼 ---------------------------------------------------------

_WHEELS = {
    "en": ("front left", "front right", "rear left", "rear right"),
    "ko": ("왼쪽 앞", "오른쪽 앞", "왼쪽 뒤", "오른쪽 뒤"),
}

# 덴트 존 내부 표현은 항상 영어 (판정 로직 "rear" 검사 등) — 표시만 변환
_ZONES_KO = {
    "front": "프론트", "front right": "프론트 우측", "right": "우측",
    "rear right": "리어 우측", "rear": "리어", "rear left": "리어 좌측",
    "left": "좌측", "front left": "프론트 좌측",
}

_CLASS_EN = {
    "hypercar": "Hypercar", "lmh": "Hypercar", "lmdh": "Hypercar",
    "lmp2": "LMP2", "lmgt3": "GT3", "gt3": "GT3", "gte": "GTE",
}
_CLASS_KO = {
    "hypercar": "하이퍼카", "lmh": "하이퍼카", "lmdh": "하이퍼카",
    "lmp2": "엘엠피 투", "lmgt3": "GT3", "gt3": "GT3", "gte": "GTE",
}

_SIDE_KO = {"left": "왼쪽", "right": "오른쪽"}

_PENALTY_KIND_KO = {
    "drive-through": "드라이브 스루", "stop-and-go": "스탑고",
    "time penalty": "타임 페널티", "penalty": "페널티",
}
_PENALTY_REASON_KO = {
    "pit lane speeding": "피트레인 과속", "track limits": "트랙 리미트",
    "yellow flag infringement": "옐로 무시", "start infringement": "스타트 반칙",
    "contact": "접촉", "blocking": "블로킹", "unsafe rejoin": "위험 복귀",
}


def wheel_name(i: int) -> str:
    return _WHEELS[LANG][i]


def wheel_display(name_en: str) -> str:
    """내부(영문) 휠 이름 → 표시 이름. 데이터에는 항상 영문을 저장한다."""
    if LANG == "ko":
        try:
            return _WHEELS["ko"][_WHEELS["en"].index(name_en)]
        except ValueError:
            return name_en
    return name_en


def zone_display(zone: str) -> str:
    if LANG == "ko":
        return _ZONES_KO.get(zone, zone)
    return zone


def class_display(cls: str) -> str:
    c = (cls or "").lower()
    table = _CLASS_KO if LANG == "ko" else _CLASS_EN
    for key, name in table.items():
        if key in c:
            return name
    return "상위 클래스" if LANG == "ko" else "faster car"


def gap_slot(gap_sec: int) -> object:
    """traffic_approach 풀의 {gap} 슬롯 — en은 "N second(s)", ko는 숫자."""
    if LANG == "ko":
        return gap_sec
    return f"{gap_sec} second" + ("s" if gap_sec > 1 else "")


def side_slot(side: str) -> str:
    """side_clear 풀의 {side} 슬롯."""
    if LANG == "ko":
        return _SIDE_KO.get(side, side)
    return side


def penalty_kind_display(kind: str) -> str:
    if LANG == "ko":
        return _PENALTY_KIND_KO.get(kind, kind)
    return kind


def penalty_reason_display(reason: str) -> str:
    if LANG == "ko":
        return _PENALTY_REASON_KO.get(reason, reason)
    return reason


def laps_text(n) -> str:
    """랩 수 + 단위 ("1 lap"/"3 laps"/"3랩") — 복수형 실수 방지용 슬롯 값."""
    n = int(round(n))
    if LANG == "ko":
        return f"{n}랩"
    return f"{n} lap" + ("s" if n != 1 else "")


def fuel_slot(n: int):
    """연료 풀의 {fuel_laps} 슬롯 — en은 단위 포함, ko는 풀에 '랩'이 있어 숫자만."""
    if LANG == "ko":
        return n
    return laps_text(n)


def seconds_text(sec: float) -> str:
    """갭 초 표기 + 단위 ("2.4 seconds"/"1 second"/"15 seconds"/"2.4초").
    10초 미만은 소수 1자리, 정각이면 소수점 생략 ("2.0"→"2") —
    엔지니어 무전 관례이자 TTS의 "이 점 영 초" 방지."""
    v = f"{sec:.1f}" if sec < 10 else f"{sec:.0f}"
    if v.endswith(".0"):
        v = v[:-2]
    if LANG == "ko":
        return f"{v}초"
    return "1 second" if v == "1" else f"{v} seconds"


def fmt_laptime(sec: float) -> str:
    """랩타임 표기 — 0.1초로 먼저 반올림한 뒤 분/초로 나눈다 (59.96초가
    "1 60.0"이 되지 않게). C# Messages.FmtLaptime 미러."""
    t10 = int(f"{sec:.1f}".replace(".", ""))
    m, r = divmod(t10, 600)
    s = f"{r // 10}.{r % 10}"
    if LANG == "ko":
        return f"{m}분 {s}초" if m >= 1 else f"{s}초"
    if m >= 1:
        # "2 01.8"은 TTS가 201.8로 읽는다 — 엔지니어식 "two oh one point eight"
        return f"{m} {'oh ' if r < 100 else ''}{s}"
    return s


# -- 문장 조립 헬퍼 (Messages.cs와 1:1 미러) ---------------------------------

_SENT_SPLIT = re.compile(r"(?<=[.!?]) +")


def split_sentences(text: str) -> list[str]:
    """문장 단위 분할 — 소수점("7.8")은 뒤에 공백이 없어 갈라지지 않는다."""
    return [s for s in _SENT_SPLIT.split(text.strip()) if s]


def and_list(items: list[str]) -> str:
    """en "a and b" / "a, b and c" — ko는 기존 "a/b" 그대로."""
    if LANG == "ko" or len(items) < 2:
        return "/".join(items)
    return ", ".join(items[:-1]) + " and " + items[-1]


def join_problems(problems: list[str]) -> str:
    """en은 문제 하나당 한 문장(첫 글자 대문자) — ko는 기존 ", " 연결 그대로."""
    if LANG == "ko":
        return ", ".join(problems)
    return ". ".join(p[:1].upper() + p[1:] for p in problems)


def multi_names(pairs: list[tuple[str, int]]) -> str:
    """(표시 이름, 대수) 쌍 → "one GT3, two Hypercars" — 순서는 호출자가 정한다."""
    parts = []
    for name, n in pairs:
        if n == 1:
            parts.append(msg("multi_one", name=name))
        elif n == 2:
            parts.append(msg("multi_two", name=name))
        else:
            parts.append(msg("multi_n", name=name, n=n))
    return ", ".join(parts)


def penalty_text(kind: str, reason) -> str:
    """페널티 콜 문장 — kind/reason은 내부(영문) 값, 표시 변환은 여기서."""
    reason_d = penalty_reason_display(reason) if reason else ""
    head = msg("penalty_head", kind=penalty_kind_display(kind)) \
        + (f", {reason_d}" if reason_d else "")
    try:
        advice = msg(f"penalty_advice_{kind}")
    except KeyError:
        advice = msg("penalty_advice_default")
    return f"{head}. {advice}"


def brief_length_text(minutes: int) -> str:
    """세션 길이 슬롯 — 1시간 이상은 정수 연산으로 반올림 (60~179분은 5분,
    180분 이상은 30분 단위). "1 minutes"/"119 minutes" 같은 표기를 없애고
    문장 사전 생성 대상을 126개 값으로 묶는다. C# Messages.BriefLengthText 미러."""
    m = int(minutes)
    if m >= 180:
        m = (m + 15) // 30 * 30
    elif m >= 60:
        m = (m + 2) // 5 * 5
    if m == 1:
        return msg("brief_minute")
    if m >= 60 and m % 60 == 0:
        h = m // 60
        return msg("brief_hours_plural" if h > 1 else "brief_hours", h=h)
    if m >= 180 and m % 60 == 30:
        return msg("brief_hours_half", h=m // 60)
    return msg("brief_minutes", m=m)


_DECIMAL = re.compile(r"(\d)\.(\d+)")


def speech_text(text: str) -> str:
    """합성 전용 정규화 — Kokoro는 "1.3"을 "one. three"로 읽는다.
    소수부는 한 자리씩 ("1.25" → "1 point 2 5", "one point twenty-five"가 아니게).
    캐시 키와 화면 표시는 원문을 쓰므로 C# 대응 없음 (C#은 Kokoro를 합성하지 않는다)."""
    text = _DECIMAL.sub(
        lambda m: m.group(1) + " point " + " ".join(m.group(2)), text)
    return text.replace("GT3s", "GT threes").replace("LMP2s", "LMP twos")


# -- 문장 테이블 ------------------------------------------------------------

_T = {
    "en": {
        # traffic
        "stopped_hazard": "Stopped car ahead. Change your line early.",
        "multi_alongside_left": "{cls} on your left",
        "multi_alongside_right": "{cls} on your right",
        "multi_alongside": "{cls} alongside",
        "multi_behind": "{names} behind",
        "multi_closing": "{names} closing",
        "multi_ahead_clear": " Ahead is clear.",
        # 한 절에 4대 이상 — 대수 조합을 다 사전 생성하지 않고 묶어 말한다
        "multi_behind_many": "Several cars behind",
        "multi_closing_many": "Several cars closing",
        "multi_one": "one {name}",
        "multi_two": "two {name}s",
        "multi_n": "{n} {name}s",
        # racecontrol
        "sector_yellow": "Yellow in sector {n}. No overtaking, be ready to lift.",
        "pen_clear": "Penalty served. Back to your race.",
        "penalty_head": "Penalty — {kind}",
        "penalty_advice_drive-through": "Serve it next lap. Mind the limiter.",
        "penalty_advice_stop-and-go": "Hold the stop time in the box. Stay calm.",
        "penalty_advice_time penalty": "Added to the result. We claw it back on pace.",
        "penalty_advice_default": "I'll call the timing.",
        # health
        "wheel_gone": "{wheel} wheel is gone! Careful, bring it to the pits slowly.",
        "part_detached_rear": "Bodywork gone at the rear. Could be the wing. "
                              "Careful next corner. If the rear goes, box.",
        "part_detached": "Bodywork detached. Possible aero loss. Checking data.",
        "wing_down": "Front aero down {mm:.0f} millimetres. Splitter damage. "
                     "Careful in the fast stuff. Repair call on pace.",
        "align_severe": "Alignment is badly out. You're steering on the straights. "
                        "Box for repairs.",
        "align_mild": "Steering pull on the straights. Alignment's off from that hit. "
                      "It'll eat the tyre. Repair call on pace.",
        "rear_instab": "Rear keeps stepping out. Looks damage-related. "
                       "Don't push, recommend box.",
        "slow_puncture": "{wheel} losing pressure. Slow puncture. We change it next stop.",
        "engine_warn": "{what} climbing. Get out of the slipstream, give it air.",
        "engine_what_water": "Water temp",
        "engine_what_oil": "Oil temp",
        "engine_what_engine": "Engine temp",
        "brake_warn": "Brakes averaging {t:.0f} degrees. Brake a touch earlier, cool them.",
        "repair_cost": "Damage is costing {delta:.1f} a lap. "
                       "We repair at the next stop. It pays off.",
        "repair_cost_big": "Damage is costing a lot of time. "
                           "We repair at the next stop. It pays off.",
        "dmg_no_effect": "That contact — no effect on pace. Forget it.",
        "report_problems": "Check done. {problems}. {advice}",
        "report_advice_box": "Prepare to box.",
        "report_advice_keep": "Keep pace, I'll make the repair call.",
        "report_marks": "Check done. Just marks. Wheels, tyres, pressures all fine. Carry on.",
        "report_clean": "Check done. No damage, car is clean. Carry on.",
        "item_wheel_damage": "{names} wheel damage",
        "item_puncture": "{names} puncture",
        "item_losing_pressure": "{name} losing pressure",
        "item_heavy_body": "heavy bodywork damage, {zones}",
        "item_heavy_body_all": "heavy bodywork damage all round",
        # tyres
        "puncture_now": "Puncture, {wheel}! Box now, take it easy.",
        # fuel
        "fuel_save": "No-stop target {target:.1f} litres a lap. "
                     "Save {delta:.1f} — lift and coast, short shift.",
        # reporter
        "last_lap_report": "Last lap {t}.",
        "best_lap_suffix": " Best lap.",
        "status_pos": "P{p}.",
        # 갭은 문장 하나씩 — 1단계 갭 문장("Gap ahead 3.2 seconds.")을 그대로 재사용
        "status_gap_ahead": "Gap ahead {gs}",
        "status_gap_behind": "Gap behind {gs}",
        "status_gaps": "{gaps}.",
        "status_gap_join": ". ",
        "status_fuel": "Fuel {n}.",
        "status_tyres": "Tyres {n}.",
        "status_tyres_good": "Tyres good.",
        # 세션 브리핑 (briefing.py) — 문장 슬롯마다 변형은 _V 표, 여기는 변형 없는 조각만
        "brief_hours": "{h} hour",
        "brief_hours_plural": "{h} hours",
        "brief_hours_half": "{h} and a half hours",
        "brief_minute": "1 minute",
        "brief_minutes": "{m} minutes",
        "brief_lap": "1 lap.",
        "brief_laps": "{n} laps.",
        "brief_cars": "{n} cars in class.",
        # 렌더러 폴백 (voice.py)
        "milestone_hours": "{h} hour{s} to go. On plan.",
        "milestone_minutes": "{m} minutes to go. Rechecking fuel and tyres.",
        "pit_window": "Pit window open. Box within {n}.",
        "stint_brief": "New stint. Easy on the tyres first lap, find the rhythm.",
        "rival_pit_base": "P{p} in class, car {rel}, just pitted.",
        "rival_rel_ahead": "ahead", "rival_rel_behind": "behind",
        "rival_pit_undercut": " Undercut attempt. I'll look at our timing.",
        "rival_pit_gap": " I'll call the gap when he's out.",
        "rival_catch": "Car ahead in class is {diff:.1f} a lap slower. "
                       "We catch him in {laps}.",
        "rival_defend": "Car behind in class is {diff:.1f} a lap quicker. "
                        "With us in {laps}. Be ready.",
        "tyre_hot": "{wheel} tyre running hot. Up {delta:.0f} degrees. Ease that side.",
        "tyre_wear": "{wheel} tyre going off. About {laps} left. Factoring it in.",
        "pace_lost": "Lost {delta:.1f} on that lap. Checking why.",
        "pace_quick": "{delta:.1f} quicker. Keep the rhythm.",
        # 갭 문장은 "Gap behind/ahead {gap}." 한 문장을 여섯 변형이 공유한다 —
        # 문장 단위 사전 생성(슬롯 하나당 한 문장)을 위해 쉼표로 잇지 말 것
        "gap_behind_closing": "Gap behind {gap}. Closing {rate:.1f} a lap. "
                              "Just no mistakes.",
        "gap_behind_opening": "Gap behind {gap}. Opening up. Good.",
        "gap_behind_holding": "Gap behind {gap}. Holding steady. Rhythm's good.",
        "gap_ahead_closing": "Gap ahead {gap}. Closing {rate:.1f} a lap. We can get him.",
        "gap_ahead_opening": "Gap ahead {gap}. Opening up. Don't overdo it.",
        "gap_ahead_holding": "Gap ahead {gap}. Holding steady. Keep the rhythm.",
        # 트레이닝 섹터 피드백 (training.LapCoach) — en 전용, ko도 이 문장으로 폴백
        "coach_quicker_best": "Sector {n}, {d:.1f} quicker, session best. Keep that feel.",
        "coach_quicker": "Sector {n}, {d:.1f} quicker. Keep that feel.",
        "coach_losing": "Losing {d:.1f} in sector {n}. Get it back next lap.",
    },
    "ko": {
        "stopped_hazard": "전방 정지 차량. 라인 미리 바꿔.",
        "multi_alongside_left": "왼쪽에 {cls} 나란히",
        "multi_alongside_right": "오른쪽에 {cls} 나란히",
        "multi_alongside": "옆에 {cls} 나란히",
        "multi_behind": "뒤에 {names} 붙는다",
        "multi_closing": "{names} 접근 중",
        "multi_ahead_clear": " 앞은 여유.",
        "multi_behind_many": "뒤에 여러 대 붙는다",
        "multi_closing_many": "여러 대 접근 중",
        "multi_one": "{name} 하나",
        "multi_two": "{name} 두 대 줄지어",
        "multi_n": "{name} {n}대",
        "sector_yellow": "섹터{n} 옐로. 추월 금지, 감속 준비.",
        "pen_clear": "페널티 클리어. 다시 니 레이스.",
        "penalty_head": "페널티 — {kind}",
        "penalty_advice_drive-through": "다음 랩 피트 통과. 리미터 주의.",
        "penalty_advice_stop-and-go": "박스 정지 시간 준수. 침착하게.",
        "penalty_advice_time penalty": "결과에 가산. 페이스로 만회.",
        "penalty_advice_default": "처리 타이밍은 내가 불러줄게.",
        "wheel_gone": "{wheel} 휠 나갔어! 스핀 조심. 천천히 피트로.",
        "part_detached_rear": "리어 쪽 보디 떨어졌어. 리어 윙일 수 있다. "
                              "다음 코너 조심. 리어 흐르면 바로 박스.",
        "part_detached": "보디 파츠 탈락. 에어로 손실 가능성. 데이터 확인 중.",
        "wing_down": "프론트 에어로 {mm:.0f}밀리 하락. 스플리터 손상. "
                     "고속 코너 조심. 수리는 페이스 보고 판단.",
        "align_severe": "얼라인 심각. 직선에서도 조향 잡아야 하는 수준. 박스에서 수리하자.",
        "align_mild": "직선에서 핸들 쏠림. 충격으로 얼라인 틀어진 듯. "
                      "타이어 편마모 주의. 수리는 페이스 보고 판단.",
        "rear_instab": "리어 불안정 반복. 데미지 영향 같아. 무리 금지, 박스 권장.",
        "slow_puncture": "{wheel} 공기압 하락 중. 슬로우 펑처. 다음 피트에서 교체.",
        "engine_warn": "{what} 상승 중. 슬립스트림에서 나와서 공기 먹이자.",
        "engine_what_water": "수온",
        "engine_what_oil": "유온",
        "engine_what_engine": "엔진 온도",
        "brake_warn": "브레이크 평균 {t:.0f}도. 브레이킹 한 템포 일찍, 식히자.",
        "repair_cost": "데미지로 랩당 {delta:.1f}초 손실. 다음 피트에 수리. 그게 이득.",
        "repair_cost_big": "데미지로 손실이 커. 다음 피트에 수리. 그게 이득.",
        "dmg_no_effect": "아까 접촉, 페이스 영향 없음. 신경 꺼도 돼.",
        "report_problems": "체크 결과. {problems}. {advice}",
        "report_advice_box": "박스 준비.",
        "report_advice_keep": "페이스 보면서 가자. 수리 판단은 내가.",
        "report_marks": "체크 완료. 가벼운 자국뿐. 휠, 타이어, 공기압 정상. 그대로 가.",
        "report_clean": "체크 완료. 데미지 없음, 차 깨끗해. 그대로 가.",
        "item_wheel_damage": "{names} 휠 손상",
        "item_puncture": "{names} 펑크",
        "item_losing_pressure": "{name} 공기압 빠지는 중",
        "item_heavy_body": "{zones} 보디 손상 심각",
        "item_heavy_body_all": "보디 전체 손상 심각",
        "puncture_now": "{wheel} 펑크! 바로 박스. 무리하지 마.",
        "fuel_save": "노피트 목표 랩당 {target:.1f}리터. "
                     "{delta:.1f}리터씩 세이브 — 리프트 앤 코스트, 숏시프트.",
        "last_lap_report": "이번 랩 {t}.",
        "best_lap_suffix": " 베스트.",
        "status_pos": "P{p}.",
        "status_gap_ahead": "앞 {g:.1f}초",
        "status_gap_behind": "뒤 {g:.1f}초",
        "status_gaps": "{gaps}.",
        "status_gap_join": ", ",
        "status_fuel": "연료 {n}.",
        "status_tyres": "타이어 {n}.",
        "status_tyres_good": "타이어 아직 좋아.",
        "brief_hours": "{h}시간",
        "brief_hours_plural": "{h}시간",
        "brief_hours_half": "{h}시간 반",
        "brief_minute": "1분",
        "brief_minutes": "{m}분",
        "brief_lap": "1랩짜리.",
        "brief_laps": "{n}랩짜리.",
        "brief_cars": "우리 클래스 {n}대.",
        "milestone_hours": "남은 시간 {h}시간{s}. 계획대로.",
        "milestone_minutes": "{m}분 남았어. 연료, 타이어 재계산 중.",
        "pit_window": "피트 윈도우 오픈. 늦어도 {n} 안에 박스.",
        "stint_brief": "새 스틴트야. 첫 랩은 타이어 아끼고, 리듬부터 찾자.",
        "rival_pit_base": "클래스 {rel} P{p} 차, 방금 피트 들어갔어.",
        "rival_rel_ahead": "앞", "rival_rel_behind": "뒤",
        "rival_pit_undercut": " 언더컷 노리는 거야. 우리 타이밍도 앞당길지 판단할게.",
        "rival_pit_gap": " 나오면 갭 다시 계산해서 불러줄게.",
        "rival_catch": "클래스 앞차, 랩당 {diff:.1f}초 느림. {laps} 안에 잡는다.",
        "rival_defend": "클래스 뒤차, 랩당 {diff:.1f}초 빠름. {laps} 뒤 도착. 준비.",
        "tyre_hot": "{wheel} 타이어 {delta:.0f}도 높음. 그쪽 코너 아껴.",
        "tyre_wear": "{wheel} 타이어 수명 {laps}. 피트 계획 반영.",
        "pace_lost": "방금 랩 {delta:.1f}초 손실. 원인 체크.",
        "pace_quick": "{delta:.1f}초 빠름. 리듬 유지.",
        "gap_behind_closing": "뒤차 랩당 {rate:.1f}초씩 접근. 갭 {gap}. 실수만 줄이자.",
        "gap_behind_opening": "뒤차 갭 {gap}. 벌어지는 중, 좋아.",
        "gap_behind_holding": "뒤차 {gap}. 유지 중. 리듬 좋아.",
        "gap_ahead_closing": "앞차 갭 {gap}. 랩당 {rate:.1f}초씩 접근 중. 갈 수 있어.",
        "gap_ahead_opening": "앞차 {gap}. 벌어지는 중. 무리 금지.",
        "gap_ahead_holding": "앞차 {gap}. 갭 유지. 리듬 그대로.",
    },
}


# -- 결정적 변형 (세션 브리핑) ---------------------------------------------------
# 문장 슬롯마다 변형 여러 개 — 고르는 건 랜덤이 아니라 스냅샷 값으로 만든 시드의 해시라
# 파이썬과 C#(Messages.Variant)이 같은 문장을 고르고, 리플레이 대조가 문장 단위로 된다.
# 시드에 세션 시계·노면 온도·연료가 들어가 같은 트랙·같은 설정이어도 세션마다 달라진다.
# 변형을 고치면 C# Messages.Variants도 같이 고치고 음성 팩을 다시 렌더한다 (세그먼트).
# ko 그룹이 없으면 en으로 폴백한다 (msg와 같은 규칙).

_V = {
    "en": {
        # 여는 말 — 트랙 이름 대신 항상 무전 체크 (세션 종류 × 시작/중간 합류)
        "brief_open_practice": (
            "Radio check. Practice session, we're all set.",
            "Radio check, one two. This is practice.",
            "Radio check. Practice is green, the car's yours.",
            "Radio check. We're in practice.",
            "Radio check. Practice, let's get some laps in.",
        ),
        "brief_open_practice_mid": (
            "Radio check. Practice is running, I'm with you.",
            "Radio check. Picking you up mid-session.",
            "Radio check, one two. Practice still live.",
        ),
        "brief_open_quali": (
            "Radio check. Qualifying session.",
            "Radio check. Qualifying, here we go.",
            "Radio check, one two. This is qualifying.",
            "Radio check. Qualifying, time to show the pace.",
            "Radio check. Qualifying is live.",
        ),
        "brief_open_quali_mid": (
            "Radio check. Qualifying is running, I'm with you.",
            "Radio check. Picking you up mid-qualifying.",
            "Radio check, one two. Qualifying still live.",
        ),
        "brief_open_warmup": (
            "Radio check. Warm-up session.",
            "Radio check. Warm-up, last look before the race.",
            "Radio check, one two. This is warm-up.",
            "Radio check. Short warm-up, nothing fancy.",
        ),
        "brief_open_warmup_mid": (
            "Radio check. Warm-up is running, I'm with you.",
            "Radio check, one two. Warm-up still live.",
            "Radio check. Picking you up in warm-up.",
        ),
        "brief_open_race": (
            "Radio check. Race session.",
            "Radio check, one two. This is the race.",
            "Radio check. Race day, here we go.",
            "Radio check. Race time.",
            "Radio check. Race on, I'm with you all the way.",
        ),
        "brief_open_race_mid": (
            "Radio check. Race is on, I'm with you.",
            "Radio check. Picking you up mid-race.",
            "Radio check, one two. Race still running.",
            "Radio check. Back on the radio, race is live.",
        ),
        # 숫자 슬롯 — 변형마다 도메인 전체가 세그먼트로 들어가므로 2~3개만
        "brief_len_long": ("{length} long.", "{length} on the clock."),
        "brief_len_remaining": ("{length} remaining.", "{length} to go."),
        "brief_grid": ("We start P{cp}.", "Starting P{cp}."),
        "brief_grid_mid": ("We're P{cp}.", "Running P{cp}."),
        "brief_temp": ("Track {t:.0f} degrees.", "Track temp {t:.0f}.",
                       "Track's at {t:.0f} degrees."),
        "brief_fuel": ("Fuel {f:.0f} litres.", "{f:.0f} litres on board."),
        "brief_rain": (
            "It's raining, watch the grip.",
            "Wet out there, grip will be low.",
            "Rain falling, be gentle early on.",
        ),
        # 연습 — 빌드업 런 계획
        "brief_plan_practice": (
            "Build it up over a few laps, no need to push yet.",
            "Bring the tyres in gently, then we start the run.",
            "Install lap first, then build the pace.",
            "Settle in, find a rhythm, then tell me about the balance.",
            "Easy first lap, then we go to work.",
        ),
        # 퀄리 — 아웃랩 공간 확보, 트랙 리미트, 마무리 (연료는 말하지 않는다)
        "brief_gap_quali": (
            "Find a gap on the out lap, we want clear air.",
            "Use the out lap to make some space.",
            "Clear air is everything. Back off if you need to.",
            "Don't start a lap stuck behind someone. Make a gap first.",
            "Out lap is about space and tyre temperature.",
        ),
        "brief_limits_quali": (
            "Mind the track limits, we can't afford a deleted lap.",
            "Four wheels inside the lines, no deleted laps.",
            "Track limits are being watched. Keep it tidy.",
            "Stay inside the white lines, a deleted lap costs us everything.",
        ),
        "brief_close_quali": (
            "Make it count.",
            "One clean lap, that's all we need.",
            "Go and get it.",
            "Clean and committed. Off you go.",
            "Deep breath, then go for it.",
        ),
        # 웜업 — 차 점검
        "brief_plan_warmup": (
            "Just check the car. Brakes, tyres, balance.",
            "Systems check before the race, no heroics.",
            "Feel for the car, save the effort for the race.",
            "Couple of laps to make sure everything's working.",
        ),
        "brief_close_out": (
            "Out when you're ready.",
            "Pit exit is yours whenever you're ready.",
            "Off you go when you're happy.",
            "Car's ready when you are.",
            "Head out when you're set.",
        ),
        # 레이스 스타트 — 포메이션(타이어·브레이크 온도, 자리 지키기, 그린 콜), 스포터 예고, 첫 랩
        "brief_formation": (
            "Formation lap, get heat in the tyres and brakes. Hold your slot, I'll call the green.",
            "On the formation lap, keep the tyres and brakes warm. "
            "Stay in your slot, the green comes from me.",
            "Formation lap. Temperature in the tyres, hold position. I'll call the green.",
            "Rolling out for the formation lap. Brakes and tyres up to temp, hold your place. "
            "I'll call it when it's green.",
            "Formation lap, warm everything up. Keep your slot, I'll call the green.",
        ),
        "brief_start_spotter": (
            "Through the start it's left and right calls only.",
            "Into turn one I'll give you side calls only.",
            "At the start, just left and right from me.",
        ),
        "brief_close_race": (
            "Calm first lap.",
            "Stay out of trouble in turn one.",
            "Keep your nose clean on lap one.",
            "Patience on the opening lap.",
            "No heroes into turn one.",
        ),
        "brief_close_mid": (
            "Carry on.",
            "Carry on with the plan.",
            "Keep doing what you're doing.",
            "Head down, carry on.",
        ),
    },
    "ko": {
        "brief_open_practice": ("무전 체크. 연습 세션이야.", "무전 체크. 연습이야, 랩 쌓아 보자."),
        "brief_open_practice_mid": ("무전 체크. 연습 진행 중, 같이 간다.",),
        "brief_open_quali": ("무전 체크. 퀄리 세션이야.", "무전 체크. 퀄리, 페이스 보여 줄 시간."),
        "brief_open_quali_mid": ("무전 체크. 퀄리 진행 중, 같이 간다.",),
        "brief_open_warmup": ("무전 체크. 웜업 세션이야.", "무전 체크. 웜업, 레이스 전 마지막 점검."),
        "brief_open_warmup_mid": ("무전 체크. 웜업 진행 중, 같이 간다.",),
        "brief_open_race": ("무전 체크. 레이스 세션이야.", "무전 체크. 레이스 간다."),
        "brief_open_race_mid": ("무전 체크. 레이스 진행 중, 같이 간다.",),
        "brief_len_long": ("{length}짜리.",),
        "brief_len_remaining": ("남은 시간 {length}.",),
        "brief_grid": ("P{cp} 스타트.",),
        "brief_grid_mid": ("지금 P{cp}.",),
        "brief_temp": ("노면 {t:.0f}도.",),
        "brief_fuel": ("연료 {f:.0f}리터.",),
        "brief_rain": ("비 오고 있어, 노면 조심.",),
        "brief_plan_practice": ("몇 랩에 걸쳐 끌어올리자, 아직 밀 필요 없어.",),
        "brief_gap_quali": ("아웃랩에서 앞뒤 공간 만들어.", "클리어 에어가 전부야, 필요하면 물러서."),
        "brief_limits_quali": ("트랙 리미트 조심, 랩 삭제되면 끝이야.",),
        "brief_close_quali": ("한 랩만 깔끔하게.", "가자, 해내자."),
        "brief_plan_warmup": ("차만 점검하자. 브레이크, 타이어, 밸런스.",),
        "brief_close_out": ("준비되면 나가자.",),
        "brief_formation": ("포메이션 랩, 타이어랑 브레이크 데워. 자리 지키고, 그린은 내가 부른다.",),
        "brief_start_spotter": ("스타트 구간은 좌우 콜 위주로 간다.",),
        "brief_close_race": ("첫 랩 침착하게.", "1코너 사고만 피하자."),
        "brief_close_mid": ("이대로 간다.",),
    },
}


def variants(key: str) -> tuple:
    """변형 목록 — 현재 언어에 없으면 en (C# Messages.VariantTemplates)."""
    vs = (_V.get(LANG) or {}).get(key)
    return vs if vs else _V["en"][key]


def variant_keys() -> tuple:
    """en 변형 그룹 키 전체 (사전 생성 열거용, 정의 순서)."""
    return tuple(_V["en"])


def fnv1a32(text: str) -> int:
    """FNV-1a 32비트 (UTF-8 바이트) — C# Messages.Fnv1a32와 같은 값.
    파이썬 hash()는 실행마다 바뀌고 C#과 다르므로 변형 선택에 쓰지 않는다."""
    h = 0x811C9DC5
    for b in text.encode("utf-8"):
        h ^= b
        h = (h * 0x01000193) & 0xFFFFFFFF
    return h


def variant_index(key: str, seed: str) -> int:
    """시드로 고른 변형 번호 — 슬롯(그룹 키)마다 따로 해시해 서로 엮이지 않게.
    해시의 상위 비트로 줄인다 ((h × n) >> 32, Lemire). `h % n`은 안 된다: FNV-1a의 하위
    k비트는 입력의 하위 k비트로만 정해져서, 키가 고정 접미사면 변형 2·4개짜리 슬롯이 전부
    같은 2비트 상태를 따라 함께 움직인다 (C# Messages.VariantIndex와 같은 식)."""
    return (fnv1a32(f"{seed}|{key}") * len(variants(key))) >> 32


def variant(key: str, seed: str, **kw) -> str:
    tpl = variants(key)[variant_index(key, seed)]
    return tpl.format(**kw) if kw else tpl


def ordinal_word(n: int) -> str:
    """레이스 요약의 {nth} 슬롯 — 2·3만 말한다 (C# Messages.OrdinalWord)."""
    if LANG == "ko":
        return {2: "2위", 3: "3위"}.get(n, "")
    return {2: "second", 3: "third"}.get(n, "")
