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


def fmt_laptime(sec: float) -> str:
    m, s = divmod(sec, 60.0)
    if LANG == "ko":
        return f"{int(m)}분 {s:.1f}초" if m >= 1 else f"{s:.1f}초"
    if m >= 1:
        # "2 01.8"은 TTS가 201.8로 읽는다 — 엔지니어식 "two oh one point eight"
        return f"{int(m)} {'oh ' if s < 10 else ''}{s:.1f}"
    return f"{s:.1f}"


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
        "multi_one": "one {name}",
        "multi_two": "two {name}s",
        "multi_n": "{n} {name}s",
        # racecontrol
        "sector_yellow": "Yellow in sector {n}. No overtaking, be ready to lift.",
        "pen_clear": "Penalty served. Back to your race.",
        "final_lap": "Last lap. Bring it home.",
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
        # tyres
        "puncture_now": "Puncture, {wheel}! Box now, take it easy.",
        # fuel
        "fuel_save": "No-stop target {target:.1f} litres a lap. "
                     "Save {delta:.1f} — lift and coast, short shift.",
        # reporter
        "last_lap_report": "Last lap {t}.",
        "best_lap_suffix": " Best lap.",
        "status_pos": "P{p}.",
        "status_gap_ahead": "ahead {g:.1f}",
        "status_gap_behind": "behind {g:.1f}",
        "status_gaps": "Gap {gaps}.",
        "status_fuel": "Fuel {n}.",
        "status_tyres": "Tyres {n}.",
        "status_tyres_good": "Tyres good.",
        # 세션 브리핑 (main)
        "kind_race": "Race", "kind_warmup": "Warmup",
        "kind_quali": "Qualifying", "kind_practice": "Practice",
        "brief_track": "{track}. {kind} session.",
        "brief_radio": "Radio check. {kind} session.",
        "brief_hours": "{h} hour",
        "brief_hours_plural": "{h} hours",
        "brief_minutes": "{m} minutes",
        "brief_len_remaining": "{length} remaining.",
        "brief_len_long": "{length} long.",
        "brief_laps": "{n} laps.",
        "brief_grid": "P{cp} of {n} in class on the grid.",
        "brief_grid_mid": "P{cp} of {n} in class.",
        "brief_rain": "It's raining, watch the grip.",
        "brief_temp": "Track {t:.0f} degrees.",
        "brief_fuel": "Fuel {f:.0f} litres.",
        "brief_carry_on": "Carry on.",
        "brief_calm": "Calm first lap.",
        "brief_out": "Out when you're ready.",
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
        "tyre_hot": "{wheel} tyre running {delta:.0f} degrees hot. Ease that side.",
        "tyre_wear": "{wheel} tyre, about {laps} left. Factoring it in.",
        "pace_lost": "Lost {delta:.1f} on that lap. Checking why.",
        "pace_quick": "{delta:.1f} quicker. Keep the rhythm.",
        "gap_behind_closing": "Car behind closing {rate:.1f} a lap. Gap {gap}. "
                              "Just no mistakes.",
        "gap_behind_opening": "Gap behind {gap} and opening. Good.",
        "gap_behind_holding": "Gap behind {gap}, holding. Rhythm's good.",
        "gap_ahead_closing": "Gap ahead {gap}, closing {rate:.1f} a lap. We can get him.",
        "gap_ahead_opening": "Gap ahead {gap} and opening. Don't overdo it.",
        "gap_ahead_holding": "Gap ahead {gap}, holding. Keep the rhythm.",
    },
    "ko": {
        "stopped_hazard": "전방 정지 차량. 라인 미리 바꿔.",
        "multi_alongside_left": "왼쪽에 {cls} 나란히",
        "multi_alongside_right": "오른쪽에 {cls} 나란히",
        "multi_alongside": "옆에 {cls} 나란히",
        "multi_behind": "뒤에 {names} 붙는다",
        "multi_closing": "{names} 접근 중",
        "multi_ahead_clear": " 앞은 여유.",
        "multi_one": "{name} 하나",
        "multi_two": "{name} 두 대 줄지어",
        "multi_n": "{name} {n}대",
        "sector_yellow": "섹터{n} 옐로. 추월 금지, 감속 준비.",
        "pen_clear": "페널티 클리어. 다시 니 레이스.",
        "final_lap": "마지막 랩. 이대로 가져오자.",
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
        "puncture_now": "{wheel} 펑크! 바로 박스. 무리하지 마.",
        "fuel_save": "노피트 목표 랩당 {target:.1f}리터. "
                     "{delta:.1f}리터씩 세이브 — 리프트 앤 코스트, 숏시프트.",
        "last_lap_report": "이번 랩 {t}.",
        "best_lap_suffix": " 베스트.",
        "status_pos": "P{p}.",
        "status_gap_ahead": "앞 {g:.1f}초",
        "status_gap_behind": "뒤 {g:.1f}초",
        "status_gaps": "{gaps}.",
        "status_fuel": "연료 {n}.",
        "status_tyres": "타이어 {n}.",
        "status_tyres_good": "타이어 아직 좋아.",
        "kind_race": "레이스", "kind_warmup": "웜업",
        "kind_quali": "퀄리", "kind_practice": "연습",
        "brief_track": "{track}, {kind} 세션이야.",
        "brief_radio": "무전 체크. {kind} 세션이야.",
        "brief_hours": "{h}시간",
        "brief_hours_plural": "{h}시간",
        "brief_minutes": "{m}분",
        "brief_len_remaining": "남은 시간 {length}.",
        "brief_len_long": "{length}짜리.",
        "brief_laps": "{n}랩짜리.",
        "brief_grid": "우리 클래스 {n}대 중 P{cp} 스타트.",
        "brief_grid_mid": "우리 클래스 {n}대 중 P{cp}.",
        "brief_rain": "비 오고 있어, 노면 조심.",
        "brief_temp": "노면 {t:.0f}도.",
        "brief_fuel": "연료 {f:.0f}리터.",
        "brief_carry_on": "이대로 간다.",
        "brief_calm": "첫 랩 침착하게.",
        "brief_out": "준비되면 나가자.",
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
        "gap_behind_closing": "뒤차 랩당 {rate:.1f}초씩 접근. 갭 {gap}초. 실수만 줄이자.",
        "gap_behind_opening": "뒤차 갭 {gap}초. 벌어지는 중, 좋아.",
        "gap_behind_holding": "뒤차 {gap}초. 유지 중. 리듬 좋아.",
        "gap_ahead_closing": "앞차 갭 {gap}초. 랩당 {rate:.1f}초씩 접근 중. 갈 수 있어.",
        "gap_ahead_opening": "앞차 {gap}초. 벌어지는 중. 무리 금지.",
        "gap_ahead_holding": "앞차 {gap}초. 갭 유지. 리듬 그대로.",
    },
}
