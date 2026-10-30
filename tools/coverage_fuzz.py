"""
커버리지 퍼즈 — 분석기 순수 조립 함수에 도메인 안팎의 값을 대량으로 넣어
"kokoro 세션에서 말할 수 있는가"를 가상 음성 팩(기대 파일명 집합)으로 판정한다.

    python tools/coverage_fuzz.py gen   --seed 56 --out cases.jsonl
    python tools/coverage_fuzz.py eval  --cases cases.jsonl --lang en --out py.tsv
    teamradio56-replay --fuzz-eval cases.jsonl --lang en --out cs.tsv   (C# 미러)
    python tools/coverage_fuzz.py check --py py.tsv --cs cs.tsv --lang en [--report-only-miss]

케이스 한 줄: {"id", "kind", "band": in|out|none, "args"} — band는 생성 시 도메인 술어로 정한다.
결과 TSV: id, kind, band, tone, outcome, text (탭·줄바꿈 이스케이프).
outcome: WHOLE | SEG | SUPPRESS:<규칙> | DROP:<정책> | MISS:<처음 빠진 문장> | NONE
(ko는 음성 팩이 없으므로 발화 행의 outcome이 "-").

게이트 (en):
  1) MISS 없음   2) band in → WHOLE|SEG
  3) band out → SUPPRESS, WHOLE/SEG(클램프), 허용 목록의 DROP만
  4) band none → NONE   5) 공통 kind는 파이썬 TSV == C# TSV (en, ko)
  6) llm_gate: 임의 문장을 못 말하는 목소리면 템플릿 + 후속(브리지) 없음
"""

from __future__ import annotations

import argparse
import json
import os
import random
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

# 의도된 무음 (kokoro 세션에서 자막만) — (kind, 정책)
DROP_ALLOW = {("lap_time", "laptime-range"), ("track_trend", "free-text")}
# 파이썬 전용 kind — C# 대조에서 뺀다
PY_ONLY = {"lap_feedback", "track_trend", "llm_gate"}

STATES = ("alongside", "nearby_behind", "approaching")
SIDES = ("left", "right", None)
RAW_CLASSES = ("Hypercar", "LMP2", "LMGT3", "Mystery Class")
WHEELS = ("front left", "front right", "rear left", "rear right")


# ── gen ─────────────────────────────────────────────────────────────────


def _r(x: float, d: int) -> float:
    return round(x, d)


def generate(seed: int) -> list:
    import speech_domain as sd
    from analyzers.racecontrol import MILESTONES_MIN
    from events import EventType as T
    from voice import PhrasePool, lines_file, iter_composed_whole_texts
    from messages import set_language

    rnd = random.Random(seed)
    cases: list = []

    def add(kind, band, **args):
        cases.append({"id": len(cases), "kind": kind, "band": band, "args": args})

    # -- 변형 풀 콜 (클램프 포함 전부 팩 안) --
    pools = {lang: PhrasePool(lines_file(lang)) for lang in ("en", "ko")}

    def tones_of(key):
        tones = set()
        for p in pools.values():
            entry = p.pools.get(key)
            if isinstance(entry, dict):
                tones.update(t for t, v in entry.items() if v)
        return sorted(tones)

    def n_lines(key, tone):
        # 두 언어 풀에 모두 있는 변형까지만 (band가 언어와 무관하게 하나로 정해지게)
        return min(len(p.lines(key, tone)) for p in pools.values())

    def pool_cases(ev, key, datas):
        for tone in tones_of(key):
            for d in datas:
                for line in range(n_lines(key, tone)):
                    add("pool", "in", ev=ev, data=d, tone=tone, line=line)

    classes = ("Hypercar", "LMH", "LMDh", "LMP2", "LMGT3", "GT3", "GTE", "Mystery", "")
    pool_cases(T.TRAFFIC_APPROACH, "traffic_approach",
               [{"cls": c, "gap_sec": g} for c in classes for g in range(0, 9)])
    for key in ("alongside", "alongside_left", "alongside_right", "nearby_behind",
                "backmarker_ahead"):
        pool_cases(T.TRAFFIC_CLOSE, key, [{"pool": key, "cls": c} for c in classes])
    for key in ("pass_complete", "dropped"):
        pool_cases(T.TRAFFIC_UPDATE, key, [{"pool": key, "cls": c} for c in classes])
    fl = [-1, 0, 0.5, 1, 1.9, 2, 3, 3.99, 4, 4.5, 10]
    pool_cases(T.FUEL_CRITICAL, "fuel_critical", [{"fuel_laps": f} for f in fl])
    pool_cases(T.FUEL_WARNING, "fuel_warning", [{"fuel_laps": f} for f in fl])
    for ev in (T.PIT_CALL, T.DAMAGE, T.PENALTY):
        pool_cases(ev, ev, [{}])
    for key in ("race_start", "fcy_start", "fcy_pit_open", "green_flag", "pit_limiter",
                "blue_flag"):
        pool_cases(T.RACE_START, key, [{"pool": key}])
    for key in ("alongside_left", "alongside_right", "alongside_both", "side_clear"):
        pool_cases(T.SPOTTER, key, [{"pool": key, "side": s} for s in ("left", "right")])
    pool_cases(T.RACE_END, "race_end", [{"class_place": p} for p in range(0, 13)])
    for key in ("position_up", "position_down"):
        pool_cases(T.POSITION_CHANGE, key,
                   [{"pool": key, "class_place": p} for p in range(0, 13)])
    add("pool", "none", ev=T.TRAFFIC_CLOSE, data={"cls": "GT3"}, tone="urgent", line=0)

    # -- 갭 --
    def gap_case(g, r, who):
        g, r = _r(g, 1), _r(r, 2)
        add("gap", "in" if sd.gap_candidate_ok(g, r) else "out", gap=g, rate=r, who=who)

    for i in range(0, 2601):
        for r in (-1.0, 0.0, 1.0):
            for who in ("ahead", "behind"):
                gap_case(i * 0.05, r, who)
    for i in range(-2400, 2401):
        for who in ("ahead", "behind"):
            gap_case(5.0, i * 0.005, who)
    for _ in range(20000):
        gap_case(rnd.uniform(0, 140), rnd.uniform(-15, 15), rnd.choice(("ahead", "behind")))

    # -- 페이스 --
    for i in range(-3000, 3001):
        d = _r(i * 0.005, 3)
        band = "none" if abs(d) < sd.PACE_MIN else ("in" if sd.pace_ok(d) else "out")
        add("pace", band, delta=d)

    # -- 라이벌 페이스 --
    for i in range(0, 1601):
        diff = _r(i * 0.005, 2)
        for mode in ("catch", "defend"):
            for laps in (1, 8, 16):
                band = ("none" if diff < sd.RIVAL_DIFF_MIN
                        else "in" if sd.rival_diff_ok(diff) else "out")
                add("rival_pace", band, mode=mode, diff=diff, laps=laps)

    # -- 라이벌 피트 --
    for their in range(1, 71):
        for off in (-2, -1, 1, 2):
            my = their + off
            if my < 1:
                continue
            add("rival_pit", "in" if sd.rival_pit_ok(their) else "out", their=their, my=my)

    # -- 타이어 --
    for i in range(0, 12001):
        d = _r(i * 0.01, 1)
        band = ("none" if d < sd.TYRE_HOT_MIN
                else "in" if sd.tyre_hot_ok(d) else "out")
        add("tyre_hot", band, delta=d, wheel=WHEELS[i % 4])
    for i in range(-100, 20001):
        n = max(_r(i * 0.01, 1), 0.0)
        add("tyre_wear", "in" if sd.tyre_wear_ok(n) else "out", laps_left=n, wheel=WHEELS[i % 4])

    # -- 피트 윈도우 --
    add("lap_analysis", "none", pit_window_laps=None)
    for n in range(0, 201):
        add("lap_analysis", "in" if sd.pit_window_ok(n) else "out", pit_window_laps=n)

    for m in MILESTONES_MIN:
        add("milestone", "in", remaining_min=m)

    # -- 고정 조립 문장 (통문장) + 페널티 메시지 말뭉치 --
    set_language("en")
    for i in range(len(list(iter_composed_whole_texts()))):
        add("fixed", "in", index=i)
    heads = ("Drive Thru Penalty", "Stop/Go Penalty", "Stop Go penalty", "10 Second Penalty",
             "5 sec penalty", "Time Penalty", "Penalty", "drive-thru penalty",
             "Drive Through")
    reasons = ("", "Pit Lane Speeding", "Pit Spd", "Cutting Track", "Track Limits",
               "Exceeding Boundaries", "Yellow Flag", "Full Course Yellow", "Caution",
               "False Start", "Jump Start", "Avoidable Contact", "Contact", "Blocking",
               "Unsafe Rejoin", "Unsportsmanlike")
    for h in heads:
        for r in reasons:
            add("penalty", "in", text=f"{h}: {r}" if r else h)
    add("penalty", "none", text="Blue flag shown")

    # -- 연료 세이브 --
    def fuel_case(fuel_l, burn, left, save):
        target = fuel_l / left
        delta = burn - target
        band = ("none" if delta < save
                else "in" if sd.fuel_save_ok(target, delta) else "out")
        add("fuel_save", band, fuel_l=fuel_l, burn=burn, left=left, save_delta=save)

    for _ in range(30000):
        left = rnd.randint(1, 60)
        fuel_case(_r(rnd.uniform(0, 140), 1), _r(rnd.uniform(0.1, 18), 2), left,
                  rnd.choice((0.1, 0.05, 0.3)))
    for left in (1, 2, 5, 10, 20):
        for t10 in range(0, 170):
            target = t10 / 10
            for s10 in (1, 5, 29, 30, 31, 40):
                fuel_case(_r(target * left, 1), _r(target + s10 / 10, 2), left, 0.1)

    # -- 데미지 리포트 --
    for mask in range(4096):
        bits = [(mask >> b) & 1 == 1 for b in range(12)]
        add("damage_report", "in", detached=bits[0:4], flat=bits[4:8], losing=bits[8:12],
            dents=[0] * 8)
    for v in range(6561):
        dents, x = [], v
        for _ in range(8):
            dents.append(x % 3)
            x //= 3
        heavy = sum(1 for s in dents if s >= 2)
        add("damage_report", "in" if heavy <= sd.DENT_MAX_ZONES else "out",
            detached=[False] * 4, flat=[False] * 4, losing=[False] * 4, dents=dents)
    for _ in range(20000):
        dents = [rnd.choice((0, 0, 0, 1, 2, 3)) for _ in range(8)]
        heavy = sum(1 for s in dents if s >= 2)
        add("damage_report", "in" if heavy <= sd.DENT_MAX_ZONES else "out",
            detached=[rnd.random() < 0.15 for _ in range(4)],
            flat=[rnd.random() < 0.2 for _ in range(4)],
            losing=[rnd.random() < 0.15 for _ in range(4)], dents=dents)

    # -- 차량 상태 --
    for i in range(0, 1501):
        d = _r(i * 0.0001, 4)
        band = "none" if d < 0.010 else ("in" if sd.wing_ok(d) else "out")
        add("wing", band, drop_m=d)
    for i in range(6000, 17001):
        avg = _r(i * 0.1, 1)
        t = int((avg / 10.0 + 0.5) // 1) * 10
        band = "none" if avg < sd.BRAKE_T_MIN else ("in" if sd.brake_ok(t) else "out")
        add("brake", band, avg=avg)
    for i in range(0, 3001):
        d = _r(i * 0.005, 3)
        band = ("none" if d < sd.REPAIR_MIN
                else "in" if sd.fixed(d, 1) <= sd.REPAIR_MAX else "out")
        add("repair", band, delta=d)

    # -- 다중 차량 --
    def multi_band(cars):
        present = [st for st in STATES if any(c[0] == st for c in cars)][:2]
        if not present:
            return "none"
        for st in present:
            if st != "alongside" and sum(1 for c in cars if c[0] == st) > sd.MULTI_MAX_CARS:
                return "out"
        return "in"

    combos = [(st, cl, sd_) for st in STATES for cl in RAW_CLASSES for sd_ in SIDES]
    for a in combos:
        for b in combos:
            add("traffic_multi", multi_band([a, b]), cars=[list(a), list(b)])
    for a in combos:
        for b in combos:
            for c in combos:
                add("traffic_multi", multi_band([a, b, c]), cars=[list(a), list(b), list(c)])
    for _ in range(20000):
        n = rnd.randint(4, 12)
        cars = [[rnd.choice(STATES + ("nearby_ahead",)), rnd.choice(RAW_CLASSES),
                 rnd.choice(SIDES)] for _ in range(n)]
        add("traffic_multi", multi_band(cars), cars=cars)

    # -- 세션 브리핑 --
    tracks = list(sd.track_names_en())

    def brief_case(**b):
        band = "in"
        track = (b.get("track") or "").strip()
        if track and not sd.known_track_en(track):
            band = "out"
        end_et, cur_et, max_laps = b["end_et"], b["cur_et"], b["max_laps"]
        if 0 < end_et < 86400:
            if max(int(round((end_et - cur_et) / 60)), 1) > sd.BRIEF_MINUTES_MAX:
                band = "out"
        elif max_laps > sd.BRIEF_LAPS_MAX:
            band = "out"
        if b["session_type"] >= 10 and b["cls_count"] > 1 \
                and not sd.brief_grid_ok(b["cls_count"], b["class_place"]):
            band = "out"
        if b["track_temp"] > 0 and not sd.brief_temp_ok(b["track_temp"]):
            band = "out"
        if b["fuel"] and not sd.brief_fuel_ok(b["fuel"]):
            band = "out"
        add("briefing", band, **b)

    def rand_brief():
        timed = rnd.random() < 0.6
        return dict(
            session_type=rnd.choice((0, 1, 5, 6, 9, 10, 11)),
            track=rnd.choice(tracks + ["", "Circuit de Test", "Imaginary Ring"]),
            end_et=_r(rnd.uniform(0, 100000), 1) if timed else 0.0,
            cur_et=_r(rnd.uniform(0, 3000), 1),
            max_laps=rnd.choice((0, 1, 5, 30, 100, 101, 500, 9999, 2147483647)),
            mid_join=rnd.random() < 0.5,
            cls_count=rnd.randint(0, 80), class_place=rnd.randint(0, 80),
            track_temp=_r(rnd.uniform(-5, 75), 1), raining=_r(rnd.random() * 0.2, 2),
            fuel=_r(rnd.choice((0.0, rnd.uniform(0, 150))), 1),
            start_spotter_sec=rnd.choice((0.0, 45.0)))

    for _ in range(30000):
        brief_case(**rand_brief())
    base = dict(session_type=10, track=tracks[0] if tracks else "", end_et=0.0, cur_et=0.0,
                max_laps=0, mid_join=False, cls_count=2, class_place=1, track_temp=25.0,
                raining=0.0, fuel=50.0, start_spotter_sec=45.0)
    for n in range(0, 81):
        for cp in (0, 1, n, n + 1):
            brief_case(**{**base, "cls_count": n, "class_place": cp})
    for i in range(-20, 800):
        brief_case(**{**base, "track_temp": i / 10})
    for i in range(0, 1500):
        brief_case(**{**base, "fuel": i / 10})
    for n in range(0, 130):
        brief_case(**{**base, "max_laps": n})
    for m in range(0, 1500, 7):
        brief_case(**{**base, "end_et": m * 60.0 + 30.0, "cur_et": 0.0})

    # -- HUD 랩타임 / 상황 리포트 --
    for i in range(3000, 40001):
        sec = _r(i * 0.01, 2)
        add("lap_time", "in" if sd.laptime_in_pack(sec) else "out",
            lap_time=sec, best=(i % 2 == 0))

    def status_case(p, ga, gb, fuel, tyre, has_tyre):
        band = "in"
        if p <= 0 and not (0 <= ga <= 60) and not (0 <= gb <= 60) and fuel is None \
                and not has_tyre:
            band = "none"
        if p > sd.CLASS_POS_MAX:
            band = "out"
        if fuel is not None and not (0 <= sd.laps_value(fuel) <= sd.STATUS_FUEL_MAX):
            band = "out"
        add("status", band, class_place=p, gap_ahead=ga, gap_behind=gb, fuel_laps=fuel,
            tyre_left=tyre, has_tyre=has_tyre)

    for _ in range(30000):
        has_tyre = rnd.random() < 0.7
        status_case(rnd.randint(0, 80), _r(rnd.uniform(-5, 80), 3), _r(rnd.uniform(-5, 80), 3),
                    rnd.choice((None, _r(rnd.uniform(0, 130), 1))),
                    rnd.choice((None, _r(rnd.uniform(0, 40), 1))) if has_tyre else None,
                    has_tyre)
    for p in range(0, 81):
        status_case(p, -1.0, -1.0, None, None, False)
    for g in range(0, 700):
        status_case(0, g / 10, -1.0, None, None, False)
        status_case(0, -1.0, g / 10, None, None, False)
    for f in range(0, 1300):
        status_case(0, -1.0, -1.0, f / 10, None, False)
    for t in range(0, 300):
        status_case(0, -1.0, -1.0, None, t / 10, True)

    add("test_speak", "in")

    # -- 파이썬 전용 --
    for sector in sd.COACH_SECTORS:
        for i in range(-1200, 1201):
            d = _r(i * 0.005, 3)
            band = ("none" if abs(d) < 0.15 else "in" if sd.coach_ok(d) else "out")
            for pb in (False, True):
                add("lap_feedback", band, sector=sector, delta=d, pb=pb)
    add("track_trend", "out", past=95.4, cur=94.1, days=3)
    add("track_trend", "out", past=93.0, cur=94.1, days=40)
    for free in (False, True):
        add("llm_gate", "in", free_text=free)
    return cases


# ── eval ────────────────────────────────────────────────────────────────


class _Car:
    __slots__ = ("state", "cls", "side")

    def __init__(self, state, cls, side):
        self.state, self.cls, self.side = state, cls, side


class _StubLLM:
    available = True

    def __init__(self):
        self.calls = 0

    def generate(self, situation):
        self.calls += 1
        return "Free text line."


def _evaluate_case(kind: str, a: dict, ctx: dict):
    """→ ("voice", tone, text) | ("suppress", rule) | ("none",) | ("drop", policy, tone, text)."""
    import speech_domain as sd
    from events import EventType as T
    from voice import VoiceGenerator, render_deterministic, iter_composed_whole_texts
    from messages import penalty_text

    if kind == "pool":
        call = VoiceGenerator.pool_call(a["ev"], a["data"])
        if call is None:
            return ("none",)
        lines = ctx["pool"].lines(call[0], a["tone"])
        if a["line"] >= len(lines):
            return ("none",)
        try:
            return ("voice", a["tone"], lines[a["line"]].format(**call[1]))
        except (KeyError, IndexError):
            return ("none",)
    if kind == "gap":
        if not sd.gap_candidate_ok(a["gap"], a["rate"]):
            return ("suppress", "gap")
        return ("voice", "casual", render_deterministic(T.GAP_COMMENT, dict(a)))
    if kind == "pace":
        if abs(a["delta"]) < sd.PACE_MIN:
            return ("none",)
        if not sd.pace_ok(a["delta"]):
            return ("suppress", "pace")
        d = {"delta": a["delta"], "direction": "slower" if a["delta"] > 0 else "faster"}
        return ("voice", "casual", render_deterministic(T.PACE_COMMENT, d))
    if kind == "rival_pace":
        if a["diff"] < sd.RIVAL_DIFF_MIN:
            return ("none",)
        if not sd.rival_diff_ok(a["diff"]):
            return ("suppress", "rival-diff")
        return ("voice", "casual", render_deterministic(T.RIVAL_PACE, dict(a)))
    if kind == "rival_pit":
        if not sd.rival_pit_ok(a["their"]):
            return ("suppress", "rival-pit")
        d = {"their_class_place": a["their"], "rel": "앞" if a["their"] < a["my"] else "뒤",
             "undercut_risk": a["their"] > a["my"]}
        return ("voice", "casual", render_deterministic(T.RIVAL_PIT, d))
    if kind == "tyre_hot":
        if a["delta"] < sd.TYRE_HOT_MIN:
            return ("none",)
        if not sd.tyre_hot_ok(a["delta"]):
            return ("suppress", "tyre-hot")
        d = {"kind": "temp_imbalance", "hot_wheel": a["wheel"], "delta": a["delta"]}
        return ("voice", "casual", render_deterministic(T.TYRE_WARNING, d))
    if kind == "tyre_wear":
        if not sd.tyre_wear_ok(a["laps_left"]):
            return ("suppress", "tyre-wear")
        d = {"kind": "wear", "wheel": a["wheel"], "laps_left": a["laps_left"]}
        return ("voice", "casual", render_deterministic(T.TYRE_WARNING, d))
    if kind == "lap_analysis":
        n = a["pit_window_laps"]
        if n is not None and not sd.pit_window_ok(n):
            return ("suppress", "pit-window")
        text = render_deterministic(T.LAP_ANALYSIS, {"pit_window_laps": n})
        return ("voice", "casual", text) if text else ("none",)
    if kind == "milestone":
        return ("voice", "casual",
                render_deterministic(T.RACE_MILESTONE, {"remaining_min": a["remaining_min"]}))
    if kind == "fixed":
        items = ctx.setdefault("fixed", list(iter_composed_whole_texts()))
        if a["index"] >= len(items):
            return ("none",)
        tone, text = items[a["index"]]
        return ("voice", tone, text)
    if kind == "penalty":
        from analyzers.racecontrol import _parse_penalty
        pen = _parse_penalty(a["text"])
        if pen is None:
            return ("none",)
        return ("voice", "urgent", penalty_text(pen[0], pen[1]))
    if kind == "fuel_save":
        from analyzers.fuel import save_message
        target = a["fuel_l"] / a["left"]
        delta = a["burn"] - target
        if delta < a["save_delta"]:
            return ("none",)
        text = save_message(a["fuel_l"], a["burn"], a["left"], a["save_delta"])
        return ("voice", "casual", text) if text else ("suppress", "fuel-save")
    if kind == "damage_report":
        from analyzers.health import damage_report_message
        text = damage_report_message(a["detached"], a["flat"], a["losing"], a["dents"])[0]
        return ("voice", "casual", text)
    if kind == "wing":
        from analyzers.health import wing_message
        if a["drop_m"] < 0.010:
            return ("none",)
        text = wing_message(a["drop_m"])
        return ("voice", "casual", text) if text else ("suppress", "wing")
    if kind == "brake":
        from analyzers.health import brake_message
        if a["avg"] < sd.BRAKE_T_MIN:
            return ("none",)
        text = brake_message(a["avg"])
        return ("voice", "casual", text) if text else ("suppress", "brake")
    if kind == "repair":
        from analyzers.health import repair_message
        if a["delta"] < sd.REPAIR_MIN:
            return ("none",)
        return ("voice", "casual", repair_message(a["delta"]))
    if kind == "traffic_multi":
        from analyzers.traffic import compose_multi
        text = compose_multi([_Car(*c) for c in a["cars"]])
        return ("voice", "urgent", text) if text else ("none",)
    if kind == "briefing":
        from briefing import BriefingInputs, briefing_text
        return ("voice", "casual", briefing_text(BriefingInputs(**a)))
    if kind == "lap_time":
        from analyzers.reporter import StatusReporter
        text = StatusReporter.laptime_text(a["lap_time"], a["best"])
        if not sd.laptime_in_pack(a["lap_time"]):
            return ("drop", "laptime-range", "casual", text)
        return ("voice", "casual", text)
    if kind == "status":
        from analyzers.reporter import StatusReporter
        text = StatusReporter.status_text(a["class_place"], a["gap_ahead"], a["gap_behind"],
                                          a["fuel_laps"], a["tyre_left"], a["has_tyre"])
        if text:
            return ("voice", "casual", text)
        # 도메인 밖 값만 있어서 전부 빠졌으면 억제, 원래 말할 게 없었으면 NONE
        if a["class_place"] > sd.CLASS_POS_MAX or \
                (a["fuel_laps"] is not None and not sd.status_fuel_ok(a["fuel_laps"])):
            return ("suppress", "status")
        return ("none",)
    if kind == "test_speak":
        return ("voice", "casual", ctx["pool"].lines("pit_call", "casual")[0])
    if kind == "lap_feedback":
        from training import LapCoach
        text = LapCoach.feedback_text(a["sector"], a["delta"], a["pb"])
        if text is None:
            return ("none",) if abs(a["delta"]) < 0.15 else ("suppress", "coach")
        return ("voice", "casual", text)
    if kind == "track_trend":
        from messages import fmt_laptime as fmt
        when = f"{a['days']} days ago" if a["days"] < 21 else f"{a['days'] // 7} weeks ago"
        text = (f"Your best here {when} was {fmt(a['past'])}. "
                f"Already {fmt(a['cur'])} today. Clear progress.")
        return ("drop", "free-text", "casual", text)
    if kind == "llm_gate":
        from events import Event, Priority
        stub = _StubLLM()
        gen = ctx["voice_gen"](lambda: a["free_text"])
        gen.llm = stub
        ev = Event(type=T.PACE_COMMENT, priority=Priority.NORMAL,
                   data={"delta": 1.2, "direction": "slower"})
        text, source = gen.text_for(ev)
        bridge = gen.bridge_text(Event(type=T.SPOTTER, priority=Priority.CRITICAL,
                                       bridge={"topic": "x"}))
        return ("llm", f"{source}:bridge={0 if bridge is None else 1}:calls={stub.calls}")
    raise ValueError(f"알 수 없는 kind: {kind}")


def _esc(s: str) -> str:
    return (s or "").replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n")


def evaluate(cases_path: str, lang: str, out_path: str) -> int:
    from messages import set_language
    from voice import PhrasePool, lines_file, PACK_TIERS
    import tts

    set_language(lang)
    ctx: dict = {"pool": PhrasePool(lines_file(lang))}

    class _Cfg:
        def __init__(self):
            self.anthropic_api_key = ""

        def get(self, key, default=None):
            return {"voice.language": lang}.get(key, default)

    def make_gen(speakable):
        from voice import VoiceGenerator

        class _State:
            narrative: list = []
            issues: dict = {}
            laps: list = []
            track = ""
            player_class = ""
            class_vehicles = 0
        return VoiceGenerator(_Cfg(), _State(), speakable=speakable)

    ctx["voice_gen"] = make_gen

    names: set = set()
    if lang == "en":
        sys.path.insert(0, os.path.join(ROOT, "tools"))
        import pack_audio
        names = pack_audio.expected_names(
            PACK_TIERS, tracks=os.path.join(ROOT, "voice_lines", "track_names_en.txt"))
        set_language(lang)
    memo: dict = {}

    def classify(tone, text):
        k = (tone, text)
        if k not in memo:
            kind, val = tts.kokoro_plan("", "bm_george", tone, text,
                                        exists=lambda p: os.path.basename(p) in names)
            memo[k] = "WHOLE" if kind == "fx" else "SEG" if kind == "seg" else f"MISS:{val}"
        return memo[k]

    t0 = time.monotonic()
    n = 0
    with open(cases_path, "r", encoding="utf-8") as f, \
            open(out_path, "w", encoding="utf-8", newline="\n") as out:
        for line in f:
            c = json.loads(line)
            r = _evaluate_case(c["kind"], c["args"], ctx)
            tone, text = "", ""
            if r[0] == "voice":
                tone, text = r[1], r[2]
                outcome = classify(tone, text) if lang == "en" else "-"
            elif r[0] == "drop":
                tone, text = r[2], r[3]
                outcome = f"DROP:{r[1]}" if lang == "en" else "-"
            elif r[0] == "suppress":
                outcome = f"SUPPRESS:{r[1]}"
            elif r[0] == "llm":
                outcome = f"LLM:{r[1]}"
            else:
                outcome = "NONE"
            out.write(f"{c['id']}\t{c['kind']}\t{c['band']}\t{tone}\t{outcome}\t{_esc(text)}\n")
            n += 1
    print(f"평가 {n}건 ({lang}, {time.monotonic() - t0:.1f}초) → {out_path}")
    return 0


# ── check ───────────────────────────────────────────────────────────────


def _read_tsv(path: str) -> dict:
    rows = {}
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            rows[int(parts[0])] = parts
    return rows


def check(py_path: str, cs_path: str, lang: str, report_only_miss: bool) -> int:
    py = _read_tsv(py_path)
    cs = _read_tsv(cs_path)
    fail = 0

    # 5) 파이썬 ↔ C# — 공통 kind 행이 바이트 단위로 같아야 한다
    shared = {i: r for i, r in py.items() if r[1] not in PY_ONLY}
    diff = [i for i in shared if cs.get(i) != shared[i]]
    extra = [i for i in cs if i not in shared]
    if diff or extra:
        fail = 1
        print(f"❌ 파이썬 ↔ C# 불일치 {len(diff)}건, C#에만 {len(extra)}건 ({lang}):")
        for i in (diff + extra)[:10]:
            print("   py:", "\t".join(py.get(i, ["-"])))
            print("   cs:", "\t".join(cs.get(i, ["-"])))
    else:
        print(f"✅ 파이썬 ↔ C# 일치 — 공통 {len(shared)}건 ({lang})")

    stats: dict = {}
    bad: list = []
    misses: dict = {}
    for i, (_, kind, band, tone, outcome, text) in sorted(py.items()):
        st = stats.setdefault(kind, {"n": 0, "in": 0, "in_ok": 0, "out": 0, "WHOLE": 0,
                                     "SEG": 0, "SUPPRESS": 0, "DROP": 0, "MISS": 0})
        st["n"] += 1
        head = outcome.split(":", 1)[0]
        if head == "MISS":
            st["MISS"] += 1
        elif band == "out" and head in st:
            st[head] += 1
        if kind == "llm_gate":
            continue
        if lang != "en":
            if band == "none" and outcome != "NONE":
                bad.append((i, kind, band, outcome, "band none인데 발화"))
            continue
        if band == "in":
            st["in"] += 1
        if head == "MISS":
            misses.setdefault(kind, []).append(outcome[5:])
            if not report_only_miss:
                bad.append((i, kind, band, outcome, text))
            continue
        if band == "in":
            if head in ("WHOLE", "SEG"):
                st["in_ok"] += 1
            else:
                bad.append((i, kind, band, outcome, text))
        elif band == "out":
            st["out"] += 1
            if head == "DROP":
                if (kind, outcome.split(":", 1)[1]) not in DROP_ALLOW:
                    bad.append((i, kind, band, outcome, text))
            elif head not in ("SUPPRESS", "WHOLE", "SEG"):
                bad.append((i, kind, band, outcome, text))
        elif band == "none" and outcome != "NONE":
            bad.append((i, kind, band, outcome, text))

    # 6) LLM 게이트 — 임의 문장을 못 말하는 목소리면 템플릿 + 브리지 없음 + 호출 0
    gate = [r for r in py.values() if r[1] == "llm_gate"]
    if gate:
        outs = sorted(r[4] for r in gate)
        want = sorted(["LLM:template:bridge=0:calls=0", "LLM:llm:bridge=1:calls=2"])
        if outs != want:
            bad.append((-1, "llm_gate", "-", " | ".join(outs), "기대: " + " | ".join(want)))

    print(f"kind별 ({lang}): 건수  in:발화%  out: 통문장/조립/억제/무음  미스")
    for kind, st in sorted(stats.items()):
        pct = 100.0 * st["in_ok"] / st["in"] if st["in"] else 100.0
        print(f"  {kind:<14} {st['n']:>7}  in:{pct:5.1f}%  out: {st['WHOLE']}/{st['SEG']}/"
              f"{st['SUPPRESS']}/{st['DROP']}  {st['MISS']}")
    for kind, ms in sorted(misses.items()):
        uniq = sorted(set(ms))
        mark = "보고" if report_only_miss else "❌"
        print(f"  {mark} 미스 {kind}: {len(ms)}건, 빠진 문장 {len(uniq)}종 — 예: {uniq[:3]}")
    if bad:
        fail = 1
        print(f"❌ 퍼즈 규칙 위반 {len(bad)}건 ({lang}):")
        for b in bad[:15]:
            print("   ", b)
    elif lang == "en":
        print("✅ 퍼즈 규칙 통과 (en)" + (" — 미스는 보고만" if report_only_miss and misses else ""))
    return fail


def main() -> int:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    g = sub.add_parser("gen")
    g.add_argument("--seed", type=int, default=56)
    g.add_argument("--out", required=True)
    e = sub.add_parser("eval")
    e.add_argument("--cases", required=True)
    e.add_argument("--lang", default="en")
    e.add_argument("--out", required=True)
    c = sub.add_parser("check")
    c.add_argument("--py", required=True)
    c.add_argument("--cs", required=True)
    c.add_argument("--lang", default="en")
    c.add_argument("--report-only-miss", action="store_true")
    args = ap.parse_args()

    if args.cmd == "gen":
        from messages import set_language
        set_language("en")
        cases = generate(args.seed)
        with open(args.out, "w", encoding="utf-8", newline="\n") as f:
            for c_ in cases:
                f.write(json.dumps(c_, ensure_ascii=False) + "\n")
        print(f"케이스 {len(cases)}건 → {args.out}")
        return 0
    if args.cmd == "eval":
        return evaluate(args.cases, args.lang, args.out)
    return check(args.py, args.cs, args.lang, args.report_only_miss)


if __name__ == "__main__":
    sys.exit(main())
