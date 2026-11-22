"""
무전 흐름 셀프테스트 (파이썬) — 세션 브리핑 변형, 체커 콜 등급, 체커 뒤 무전 규칙, 피니시 뒤 침묵.

  1  FNV-1a 32비트 테스트 벡터 (C# Messages.Fnv1a32와 같은 값이어야 변형이 같다)
  2  골든 브리핑 — 세션 종류별 시작/중간 합류 문장 전체 (해시·변형 표를 고정)
  3  결정성 — 같은 입력이면 같은 문장
  4  도달성 — 세션 시계를 훑으면 그룹마다 모든 변형이 한 번 이상 쓰이고, 나온 문장은
     전부 문장 세그먼트 목록(iter_pregen_sentences)에 있다
  5  구성 — 세션 종류별 문장 그룹 순서 (연습: 연료·그리드·포메이션 없음, 퀄리: 연료 없음·
     트랙 리미트 항상, 레이스 스타트: 그리드·포메이션, 레이스 중간 합류: 포메이션 없음),
     BriefingInputs에 트랙 이름이 없다
     차고 합류(중간 합류 아님, 시계 120초 넘음)는 시작 구성에 '남은 시간'
  6  다양성 — 레이스 스타트 브리핑 500개 시드에서 서로 다른 문장 100개 이상, 그리고 슬롯끼리
     독립: 세션 종류(시작/중간 합류)마다 변형 슬롯 두 개씩 n_a × n_b 조합이 전부 나온다
     (`h % n`으로 줄이면 FNV-1a 하위 비트 때문에 변형 2·4개 슬롯이 함께 움직였다)
  7  체커 콜 문장 — 4·9·12·60위는 "P{순위}." 그대로 (8로 자르지 않는다), 계단 수, 포디움
  8  순수 규칙 표 — finish_call, summary_pool, cooldown_pool
  9  피니시 뒤 침묵 — 연료·페이스·타이어·전략·리포터·라이벌·차량 상태·섹터 코칭이
     내 레이스가 끝나면(체커·DNF) 아무것도 내지 않는다. 같은 데이터로 레이스 중이면
     콜이 나오는지도 확인한다 (픽스처가 실제로 콜을 일으키는지)

C# 쪽은 커버리지 퍼즈(브리핑·체커 등급·요약·쿨다운 kind)와 리플레이 대조가 같은 결과를 요구한다.

사용법: python tools/radio_flow_selftest.py     종료 코드: 0 = 통과, 1 = 실패
"""

from __future__ import annotations

# Windows 콘솔(cp949) 인코딩 가드
import sys
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

import dataclasses
import os

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from messages import (fnv1a32, set_language, variant_index,             # noqa: E402
                      variant_keys, variants)

set_language("en")

from briefing import (BriefingInputs, briefing_parts, briefing_seed,    # noqa: E402
                      briefing_text, session_kind)
from race_end import cooldown_pool, finish_call, summary_pool          # noqa: E402

errors: list[str] = []


def check(cond: bool, what: str) -> None:
    if not cond:
        errors.append(what)


# ── 1. FNV-1a ───────────────────────────────────────────────────────────
for text, want in (("", 0x811C9DC5), ("a", 0xE40C292C), ("foobar", 0xBF9CF968),
                   ("10|0|12.4|3600|2147483647|31.0|70.0|9|5|brief_open_race", None)):
    got = fnv1a32(text)
    if want is not None:
        check(got == want, f"fnv1a32({text!r}) = {got:#x}, 기대 {want:#x}")
check(fnv1a32("무전") == fnv1a32("무전") and fnv1a32("무전") != fnv1a32("무 전"),
      "fnv1a32 UTF-8 처리")

# ── 2. 골든 브리핑 ──────────────────────────────────────────────────────
GOLDEN = [
    (dict(session_type=1, end_et=3600.0, cur_et=12.4, track_temp=27.0, fuel=60.0),
     "Radio check. We're in practice. 1 hour long. Track 27 degrees. Easy first lap, then "
     "we go to work. Head out when you're set."),
    (dict(session_type=2, end_et=5400.0, cur_et=600.0, mid_join=True, track_temp=33.6,
          fuel=41.5),
     "Radio check, one two. Practice still live. 80 minutes remaining. Track's at 34 "
     "degrees. Keep doing what you're doing."),
    (dict(session_type=5, end_et=1200.0, cur_et=3.2, track_temp=29.4, raining=0.2, fuel=22.0),
     "Radio check, one two. This is qualifying. 20 minutes long. Track 29 degrees. Rain "
     "falling, be gentle early on. Out lap is about space and tyre temperature. Track "
     "limits are being watched. Keep it tidy. Deep breath, then go for it."),
    (dict(session_type=6, end_et=1800.0, cur_et=840.6, mid_join=True, track_temp=18.2,
          raining=0.3, fuel=30.0),
     "Radio check. Picking you up mid-qualifying. 16 minutes remaining. Track's at 18 "
     "degrees. Rain falling, be gentle early on. Mind the track limits, we can't afford a "
     "deleted lap. Head down, carry on."),
    (dict(session_type=9, end_et=900.0, cur_et=5.0, track_temp=24.0, fuel=85.0),
     "Radio check, one two. This is warm-up. 15 minutes long. Track 24 degrees. Systems "
     "check before the race, no heroics. Car's ready when you are."),
    (dict(session_type=10, end_et=21600.0, cur_et=31.8, cls_count=14, class_place=6,
          track_temp=35.2, fuel=88.0, start_spotter_sec=45.0),
     "Radio check. Race time. 6 hours on the clock. 14 cars in class. We start P6. Track's "
     "at 35 degrees. 88 litres on board. Rolling out for the formation lap. Brakes and "
     "tyres up to temp, hold your place. I'll call it when it's green. Through the start "
     "it's left and right calls only. Keep your nose clean on lap one."),
    (dict(session_type=12, max_laps=30, cur_et=8.6, cls_count=9, class_place=1, track_temp=22.7,
          raining=0.4, fuel=55.0, start_spotter_sec=0.0),
     "Radio check. Race on, I'm with you all the way. 30 laps. 9 cars in class. Starting "
     "P1. Track 23 degrees. Wet out there, grip will be low. 55 litres on board. Formation "
     "lap, warm everything up. Keep your slot, I'll call the green. Patience on the "
     "opening lap."),
    (dict(session_type=10, end_et=86000.0, cur_et=5400.2, mid_join=True, cls_count=20,
          class_place=12, track_temp=41.0, fuel=47.3),
     "Radio check. Race is on, I'm with you. 22 and a half hours to go. 20 cars in class. "
     "Running P12. Track's at 41 degrees. Fuel 47 litres. Carry on with the plan."),
    # 진행 중인 퀄리에 차고에서 합류 (mid_join=False, 시계 120초 넘음) — 시작 구성, '남은 시간'
    (dict(session_type=6, end_et=3600.0, cur_et=900.4, track_temp=28.3, fuel=30.0),
     "Radio check. Qualifying, here we go. 45 minutes remaining. Track temp 28. Use the "
     "out lap to make some space. Stay inside the white lines, a deleted lap costs us "
     "everything. Make it count."),
]
for args, want in GOLDEN:
    got = briefing_text(BriefingInputs(**args))
    check(got == want, f"골든 브리핑 {args}:\n      {got}\n   기대 {want}")
    check("Radio check" in got.split(".")[0], f"무전 체크로 열지 않음: {got}")

# ── 3. 결정성 ───────────────────────────────────────────────────────────
for args, _ in GOLDEN:
    texts = {briefing_text(BriefingInputs(**args)) for _ in range(3)}
    check(len(texts) == 1, f"같은 입력인데 문장이 다름: {args}")

# ── 4. 도달성 + 5. 구성 ─────────────────────────────────────────────────
from voice import iter_pregen_sentences                                # noqa: E402
from messages import split_sentences                                   # noqa: E402

pack = {s for tone, s in iter_pregen_sentences() if tone == "casual"}
ORDER = {
    ("practice", False): ["brief_open_practice", "brief_len_long", "brief_temp", "brief_rain",
                          "brief_plan_practice", "brief_close_out"],
    ("practice", True): ["brief_open_practice_mid", "brief_len_remaining", "brief_temp",
                         "brief_rain", "brief_close_mid"],
    ("quali", False): ["brief_open_quali", "brief_len_long", "brief_temp", "brief_rain",
                       "brief_gap_quali", "brief_limits_quali", "brief_close_quali"],
    ("quali", True): ["brief_open_quali_mid", "brief_len_remaining", "brief_temp", "brief_rain",
                      "brief_limits_quali", "brief_close_mid"],
    ("warmup", False): ["brief_open_warmup", "brief_len_long", "brief_temp", "brief_rain",
                        "brief_plan_warmup", "brief_close_out"],
    ("warmup", True): ["brief_open_warmup_mid", "brief_len_remaining", "brief_temp",
                       "brief_rain", "brief_close_mid"],
    ("race", False): ["brief_open_race", "brief_len_long", "brief_cars", "brief_grid",
                      "brief_temp", "brief_rain", "brief_fuel", "brief_formation",
                      "brief_start_spotter", "brief_close_race"],
    ("race", True): ["brief_open_race_mid", "brief_len_remaining", "brief_cars",
                     "brief_grid_mid", "brief_temp", "brief_rain", "brief_fuel",
                     "brief_close_mid"],
}
used: dict = {}
for stype in (1, 5, 9, 10):
    for mid in (False, True):
        kind = session_kind(stype)
        for i in range(400):
            b = BriefingInputs(session_type=stype, end_et=3600.0, cur_et=i * 0.1, mid_join=mid,
                               cls_count=9, class_place=5, track_temp=31.0,
                               raining=0.3, fuel=70.0, start_spotter_sec=45.0)
            parts = briefing_parts(b)
            keys = [k for k, _ in parts]
            if i == 0:
                check(keys == ORDER[(kind, mid)],
                      f"구성 {kind} mid={mid}: {keys}\n   기대 {ORDER[(kind, mid)]}")
            seed = briefing_seed(b)
            for k, text in parts:
                if k in variant_keys():
                    used.setdefault(k, set()).add(variant_index(k, seed))
                for s in split_sentences(text):
                    check(s in pack, f"음성 팩 문장 목록에 없음: {s!r} ({k})")
for k in variant_keys():
    n_var = len(variants(k))
    check(len(used.get(k, ())) == n_var,
          f"변형 미사용 {k}: {len(used.get(k, ()))}/{n_var}")
check(not any(f.name == "track" for f in dataclasses.fields(BriefingInputs)),
      "BriefingInputs에 track이 남아 있음")
for (kind, mid), keys in ORDER.items():
    if kind != "race":
        check("brief_fuel" not in keys and "brief_formation" not in keys
              and "brief_grid" not in keys, f"{kind}: 연료/포메이션/그리드가 들어감")
check("brief_limits_quali" in ORDER[("quali", True)], "퀄리 중간 합류에 트랙 리미트 없음")
# 진행 중인 세션에 차고에서 합류 — 중간 합류가 아니니 시작 구성, 길이만 '남은 시간'
# (레이스는 페이즈로 판정하므로 그대로 '길이')
for stype in (1, 5, 9, 10):
    kind = session_kind(stype)
    b = BriefingInputs(session_type=stype, end_et=3600.0, cur_et=900.0, cls_count=9,
                       class_place=5, track_temp=31.0, raining=0.3, fuel=70.0,
                       start_spotter_sec=45.0)
    keys = [k for k, _ in briefing_parts(b)]
    want = [("brief_len_long" if kind == "race" else "brief_len_remaining")
            if k == "brief_len_long" else k for k in ORDER[(kind, False)]]
    check(keys == want, f"차고 합류 구성 {kind}: {keys}\n   기대 {want}")

# ── 6. 다양성 ───────────────────────────────────────────────────────────
texts = {briefing_text(BriefingInputs(session_type=10, end_et=3600.0, cur_et=i * 0.37,
                                      cls_count=9, class_place=5, track_temp=31.0,
                                      fuel=70.0, start_spotter_sec=45.0))
         for i in range(500)}
check(len(texts) >= 100, f"레이스 스타트 브리핑 다양성 부족: {len(texts)}종/500")
pairs_checked = 0
for stype in (1, 5, 9, 10):
    for mid in (False, True):
        kind = session_kind(stype)
        seen: dict = {}
        slots: list = []
        for i in range(3000):
            b = BriefingInputs(session_type=stype, end_et=3600.0 + 60 * (i % 7),
                               cur_et=(300.0 if mid else 0.0) + i * 0.37, mid_join=mid,
                               cls_count=9, class_place=1 + i % 8,
                               track_temp=20.0 + (i % 23) * 0.7, raining=0.3,
                               fuel=40.0 + (i % 31) * 1.3, start_spotter_sec=45.0)
            seed = briefing_seed(b)
            slots = [k for k, _ in briefing_parts(b) if k in variant_keys()]
            idx = [variant_index(k, seed) for k in slots]
            for x in range(len(slots)):
                for y in range(x + 1, len(slots)):
                    seen.setdefault((slots[x], slots[y]), set()).add((idx[x], idx[y]))
        for (ka, kb), got in seen.items():
            n = len(variants(ka)) * len(variants(kb))
            pairs_checked += 1
            check(len(got) == n, f"변형 슬롯이 엮임 {kind} mid={mid}: {ka} × {kb} "
                                 f"조합 {len(got)}/{n}")

# ── 7. 체커 콜 문장 ─────────────────────────────────────────────────────
from events import EventType                                           # noqa: E402
from voice import PhrasePool, VoiceGenerator, lines_file               # noqa: E402

pool = PhrasePool(lines_file("en"))


def finish_lines(place, start, multi=False):
    p, s, g = finish_call(place, start, multi)
    key, slots = VoiceGenerator.pool_call(EventType.RACE_END,
                                          {"pool": p, "class_place": place,
                                           "start_place": s, "gained": g})
    return key, [ln.format(**slots) for ln in pool.lines(key)]


for place, start in ((4, 9), (9, 15), (12, 4), (60, 60), (9, 0), (12, 0)):
    key, lines = finish_lines(place, start)
    check(bool(lines), f"체커 콜 풀 비어 있음: {key}")
    for ln in lines:
        check(f"P{place}." in ln.split(" ") or ln.startswith(f"P{place}."),
              f"{key} P{place}: 순위 문장 없음 — {ln}")
        check(place == 8 or "P8." not in ln, f"{key} P{place}가 P8로: {ln}")
key, lines = finish_lines(5, 9)
check(key == "race_end_gained" and all("4 places" in ln for ln in lines),
      f"gained 계단 수: {key} {lines}")
for place in (2, 3):
    key, lines = finish_lines(place, 9)
    check(key == "race_end_podium" and all(f"P{place}" in ln for ln in lines),
          f"포디움 P{place}: {key}")
key, lines = finish_lines(61, 70)
check(key == "race_end_plain" and not any("P6" in ln for ln in lines), f"범위 밖 순위: {key}")
for pk in ("position_up", "position_down"):
    _, slots = VoiceGenerator.pool_call(EventType.POSITION_CHANGE,
                                        {"pool": pk, "class_place": 12})
    check(slots == {"place": "12"}, f"{pk} 순위 12가 잘림: {slots}")

# ── 8. 순수 규칙 표 ─────────────────────────────────────────────────────
FINISH = [
    ((1, 5, False), ("race_end_win", 5, 4)), ((1, 0, True), ("race_end_class_win", 0, 0)),
    ((2, 9, False), ("race_end_podium", 9, 7)), ((3, 0, True), ("race_end_podium", 0, 0)),
    ((4, 0, False), ("race_end_result", 0, 0)), ((5, 9, False), ("race_end_gained", 9, 4)),
    ((5, 7, False), ("race_end_gained", 7, 2)), ((5, 6, False), ("race_end_held", 6, 1)),
    ((5, 5, False), ("race_end_held", 5, 0)), ((5, 4, False), ("race_end_held", 4, -1)),
    ((12, 4, False), ("race_end_lost", 4, -8)), ((60, 1, False), ("race_end_lost", 1, -59)),
    ((4, 61, False), ("race_end_result", 0, 0)), ((0, 5, False), ("race_end_plain", 0, 0)),
    ((61, 5, False), ("race_end_plain", 0, 0)),
]
for args, want in FINISH:
    check(finish_call(*args) == want, f"finish_call{args} = {finish_call(*args)}, 기대 {want}")
SUMMARY = [
    ((1, 2, False), "race_summary_fastest"), ((1, 2, True), "race_summary_fastest_class"),
    ((1, 1, False), "race_summary_debrief"), ((2, 3, False), "race_summary_pace"),
    ((3, 3, True), "race_summary_pace_class"), ((4, 9, False), "race_summary_debrief"),
    ((0, 0, False), "race_summary_debrief"),
]
for args, want in SUMMARY:
    check(summary_pool(*args) == want, f"summary_pool{args} = {summary_pool(*args)}, 기대 {want}")
for args, want in (((0, False), "cooldown_lap"), ((2, False), "cooldown_lap_traffic"),
                   ((1, True), "cooldown_lap_faster"), ((0, True), "cooldown_lap_faster")):
    check(cooldown_pool(*args) == want, f"cooldown_pool{args}")

# ── 9. 피니시 뒤 침묵 ───────────────────────────────────────────────────
from config import load_config                                         # noqa: E402
from events import EventBus                                            # noqa: E402
from state import LapRecord, SessionState                              # noqa: E402

cfg = load_config("__defaults_only__")
cfg._data["reports"]["laptime_every_lap"] = True


class _Snap:
    def __init__(self, rain=0.0, water=88.0, rival_last=81.0):
        self.t = 500.0
        self.session = {"session_type": 10, "game_phase": 5, "raining": rain,
                        "avg_wetness": rain, "track_temp": 30.0, "track_len": 2400.0,
                        "current_et": 500.0, "end_et": 3600.0}
        self.player = {"fuel": 5.0, "water_temp": water, "oil_temp": 100.0,
                       "overheating": False, "wheels": [
                           {"flat": False, "detached": False, "brake_temp": 300.0}] * 4}
        self.vehicles = [
            {"id": 0, "is_player": True, "cls": "GT3", "place": 2, "in_pits": False,
             "finish_status": 0, "last_lap": 80.0, "driver": "나"},
            {"id": 1, "is_player": False, "cls": "GT3", "place": 1, "in_pits": False,
             "finish_status": 0, "last_lap": rival_last, "driver": "리바이"},
        ]

    def player_scoring(self):
        return self.vehicles[0]


def make_state(over: str) -> SessionState:
    st = SessionState()
    st.session_type = 10
    st.track = "Circuit de Test"
    wear = 1.0
    for n in range(1, 8):
        wear = round(wear - 0.12, 2)
        t = 81.5 if n == 7 else 80.0
        st.laps.append(LapRecord(
            lap_number=n, lap_time=t, s1=26.0 + (0.5 if n == 7 else 0.0), s2=27.0,
            s3=t - 53.0 - (0.5 if n == 7 else 0.0), place=2, class_place=2,
            fuel_left=40.0 - 2.5 * n, fuel_used=2.5, gap_ahead=3.0, gap_behind=4.0,
            tyre_wear=[wear] * 4, tyre_temps=[85.0] * 4, brake_temps=[300.0] * 4,
            in_pits=False, track_temp=30.0, raining=0.0, valid=True))
    st.race_end.laps_left = 5
    if over == "finished":
        st.race_end.finished = True
    elif over == "out":
        st.race_end.out = True
    return st


def run(name, over):
    """분석기 하나를 픽스처로 돌려 수락된 이벤트 종류 목록."""
    from analyzers.fuel import FuelAnalyzer
    from analyzers.health import HealthAnalyzer
    from analyzers.pace import PaceAnalyzer
    from analyzers.reporter import StatusReporter
    from analyzers.rivals import RivalAnalyzer
    from analyzers.strategy import StrategyEngine
    from analyzers.tyres import TyreAnalyzer
    from training import LapCoach

    st = make_state(over)
    bus = EventBus(cfg["cooldowns"])
    got = []
    orig = bus.push

    def push(ev):
        ok = orig(ev)
        if ok:
            got.append(ev.type)
        return ok

    bus.push = push
    snap = _Snap()
    if name == "fuel":
        FuelAnalyzer(cfg).on_lap(st, snap, bus)
    elif name == "pace":
        PaceAnalyzer(cfg).on_lap(st, snap, bus, st.laps[-1])
    elif name == "tyres":
        TyreAnalyzer(cfg).on_lap(st, snap, bus)
    elif name == "strategy":
        eng = StrategyEngine(cfg)
        finished, out = st.race_end.finished, st.race_end.out
        st.race_end.finished = st.race_end.out = False       # 기준 강수 (레이스 중)
        eng.on_lap(st, snap, bus, None, None)
        st.race_end.finished, st.race_end.out = finished, out
        eng.on_lap(st, _Snap(rain=0.6), bus, None, None)
    elif name == "reporter":
        StatusReporter(cfg).on_lap(st, snap, bus, None, None)
    elif name == "rivals":
        r = RivalAnalyzer(cfg)
        r.on_lap(st, _Snap(rival_last=81.0), bus)
        r.on_lap(st, _Snap(rival_last=81.2), bus)
    elif name == "health":
        HealthAnalyzer(cfg).on_lap(st, _Snap(water=112.0), bus)
    elif name == "coach":
        LapCoach().on_lap(st, bus)
    return got


for name in ("fuel", "pace", "tyres", "strategy", "reporter", "rivals", "health", "coach"):
    live = run(name, "")
    check(bool(live), f"픽스처가 레이스 중 콜을 못 일으킴: {name}")
    for over in ("finished", "out"):
        got = run(name, over)
        check(not got, f"{name}: 내 레이스가 끝났는데({over}) 콜 {got}")

if errors:
    print(f"❌ 무전 흐름 셀프테스트 실패 {len(errors)}건:")
    for e in errors[:30]:
        print("   " + e)
    sys.exit(1)
print(f"✅ 무전 흐름 셀프테스트 통과 — 골든 브리핑 {len(GOLDEN)}개, 변형 그룹 {len(variant_keys())}개 "
      f"전부 도달, 레이스 스타트 {len(texts)}종/500, 슬롯 쌍 {pairs_checked}개 독립, "
      f"체커 등급·요약·쿨다운 규칙, 피니시 뒤 침묵 8종")
