"""
얼라인(조향 쏠림) 콜 회귀용 합성 리플레이 생성기 — 리플레이 + 기대 콜(expect) 사이드카.

사용자 제보: "스티어링 풀(Steering pull on the straights…)이 차가 멀쩡할 때도 나온다".
예전 감지는 150km/h 이상·|조향| ≤ 0.20 샘플 전부의 EMA를 충격 순간 값과 75샘플 뒤에
비교했다. |조향| 0.20은 핸들 70~110°라 고속 스위퍼(조향 0.1~0.18)가 전부 '직선'으로 들어가고,
EMA가 충격 지점과 15초 뒤 지점의 코너 구성(좌우 비율) 차이를 오프셋 변화로 읽었다 —
스위퍼 구간 직전 가벼운 접촉 하나로 멀쩡한 차에 콜. 녹화 리플레이에는 조향·요레이트·횡속도
필드가 아예 없어서(race_full 등) 이 감지는 한 번도 재생된 적이 없다.

여기서는 한 레이스에 경우를 모두 넣고(경우 사이는 피트 진입 = 얼라인 상태 리셋), 어떤 콜이
몇 초에 나와야 하는지를 data/replays/align.expect.json에 같이 쓴다 (tools/expect_calls.py가
파이썬·C# 둘 다 대조 — align_damage/align_severe 키만).

규칙 (기대값의 근거, analyzers/health.py STEER_* · C# HealthAnalyzer와 같은 값):
  · 샘플 = 피트레인 밖 · 150km/h 이상 · |요레이트|×속도 ≤ 1.5m/s² · |횡속도| ≤ 1.0m/s ·
    |조향| ≤ 0.20 — 차가 실제로 곧게 가는 순간만 (스위퍼 안은 빠지고, 스위퍼 사이 짧은
    직선·코너 시작 틱은 들어간다)
  · 기준 = 첫 충격 전 최근 101샘플 중앙값, 25샘플 미만이면 기준 없음 → 그 스틴트는 콜 없음
  · 판정 = 충격 뒤 새 버퍼 51샘플이 찼을 때 중앙값 − 기준: ≥ 0.03 경미, ≥ 0.08 심각
  · 타이어 펑크/탈락·공기압 누출 중엔 콜하지 않는다 (쏠림은 타이어 탓)
  · 판정 시각은 점검 리포트(충격 +8초)의 damage_report 쿨다운 20초 밖 — 재시도 없이 그 틱

모델: 혼자 달리는 레이스(시간제). 랩 = 직선 S1(290) · 시케인(110) · 직선 S2(250) · 고속 스위퍼
구간 F(200km/h, 오른쪽 위주 4코너, 조향 최대 0.17) · 중속 코너(130) · 직선 S3(270) · 헤어핀(80).
구간 안은 등속이고 코너는 반 사인 곡률. 조향 = 오프셋 + GAIN × 곡률 + 손떨림, 요레이트 = 속도 ×
곡률, 횡속도 = 0.06 × 횡가속 + 작은 흔들림 — 손상된 차도 드라이버가 오프셋을 잡고 곧게 가므로
직선의 요레이트는 0 근처. 피트 랩은 S1 자리를 피트레인(~78km/h, 12초 정지)으로 달린다. 구간 경계는 전부 틱 위라 코너 첫 틱은
곡률 0(곧게 감), 다음 틱부터 확실히 코너다.
위치·시각은 구간 표에서 해석적으로 구한다 (dt 누적 없음 — 재생성 결과가 머신과 무관하게 같다).

사용법: python tools/make_align_replay.py [--out-dir data/replays] [--check]
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

import argparse
import gzip
import json
import math
import os
import tempfile

NAME = "align"
HZ = 5
INT_MAX = 2147483647
END_ET = 3000.0             # 시간제 50분 — 리플레이(~19분) 안에 하프·남은 시간 안내가 없다
T_FORMATION_END = 20.0      # 0~20 페이즈 3, 20~30 페이즈 4 (카운트다운), 30부터 그린
T_GREEN = 30.0
GRID = -30.0                # 그리드 위치 (라인 뒤 m)
IMPACT_MAG = 700.0          # damage_impact_mag(500) 이상, 큰 충격(×4) 아님 — 가벼운 접촉
REPORT_DELAY = 8.0          # health.REPORT_DELAY_SEC
REPORT_COOLDOWN = 20.0      # config cooldowns.damage_report

# 감지 규칙 (analyzers/health.py STEER_*)
SPEED_MIN = 150.0
STEER_MAX = 0.20
LAT_ACC_MAX = 1.5
LAT_VEL_MAX = 1.0
SHIFT_MIN = 0.03
SHIFT_SEVERE = 0.08
BASE_N = 101
BASE_MIN = 25
JUDGE_N = 51
# 예전 감지 (EMA) — 경우 (a)가 예전 코드에서 콜이 나는 모양인지 확인용
OLD_ALPHA = 0.02
OLD_JUDGE = 75

GAIN = 21.0                 # 조향(락 대비) / 곡률(1/m) — 고속 언더스티어 포함 근사
SLIP = 0.06                 # 횡속도(m/s) / 횡가속(m/s²) — 정상 코너링 1~2m/s

SAY_MILD = ("Steering pull on the straights. Alignment's off from that hit. "
            "It'll eat the tyre. Repair call on pace.")
SAY_SEVERE = "Alignment is badly out. You're steering on the straights. Box for repairs."

# 구간: (이름, 종류, 시간 s, km/h, 코너 목록) — 코너 = (시작 s, 길이 s, 건강한 차의 최대 조향)
SWEEPERS = [(0.0, 5.0, 0.15), (6.0, 3.0, -0.10), (10.0, 6.0, 0.17), (17.0, 4.0, 0.12)]
LAP = [
    ("S1", "straight", 14.0, 290.0, []),
    ("CH", "corner", 6.0, 110.0, [(0.0, 3.0, 0.32), (3.0, 3.0, -0.32)]),
    ("S2", "straight", 8.0, 250.0, []),
    ("F", "corner", 22.0, 200.0, SWEEPERS),
    ("MC", "corner", 5.0, 130.0, [(0.0, 5.0, 0.22)]),
    ("S3", "straight", 12.0, 270.0, []),
    ("HP", "corner", 6.0, 80.0, [(0.0, 6.0, 0.45)]),
]
TL = sum(dur * kmh / 3.6 for _, _, dur, kmh, _ in LAP)
PIT_HALF_S = 26.0           # 피트레인 반 구간 시간 (S1 길이의 반, ~78km/h) — 구간 경계가 틱에 맞게
PIT_STOP_S = 12.0
LAP_S = sum(seg[2] for seg in LAP)

# 랩 계획 — 랩 번호(0부터)별: pit = 이 랩 S1을 피트레인으로, impact = (경우 이름, 구간, 구간 안 시각)
# 충격 뒤 상태(offset 조향 오프셋, flat 우측 앞 펑크)는 다음 피트 진입까지 유지된다.
CASES = [
    dict(name="healthy_sweepers", lap=1, seg="S2", at=7.5, offset=0.0, expect=None,
         note="멀쩡한 차 + 스위퍼 구간 직전 가벼운 접촉 → 콜 없음 "
              "(예전: 스위퍼 조향이 EMA에 섞여 align_mild 오탐)"),
    dict(name="no_baseline", lap=2, pit=True, seg="S2", at=3.0, offset=0.05, expect=None,
         note="피트 아웃 직후(기준 직선 샘플 16개 < 25) 충격 + 실제 오프셋 +0.05 → 기준 없음, 콜 없음"),
    dict(name="flat_tyre_pull", lap=5, seg="S2", at=7.5, offset=0.05, flat=True, expect=None,
         note="충격 + 우측 앞 펑크로 +0.05 쏠림 → 타이어 탓, 얼라인 콜 없음 (점검 리포트가 펑크를 부른다)"),
    dict(name="align_mild", lap=7, seg="S2", at=7.5, offset=0.05, expect="mild",
         note="충격 뒤 직선 조향 오프셋 +0.05 → align_mild 하나"),
    dict(name="align_severe", lap=9, seg="S2", at=7.5, offset=0.10, expect="severe",
         note="충격 뒤 직선 조향 오프셋 +0.10 → align_severe 하나"),
]
PIT_LAPS = {2, 4, 6, 8}     # 경우 사이 피트 = 얼라인·타이어 리셋 (수리)
N_LAPS = 11                 # 마지막 랩(10)은 S1 끝까지만 달린다


class Seg:
    def __init__(self, name, kind, t0, dur, v, d0, corners, lap, pit):
        self.name, self.kind, self.t0, self.dur, self.v = name, kind, t0, dur, v
        self.d0, self.corners, self.lap, self.pit = d0, corners, lap, pit
        self.t1 = t0 + dur
        self.d1 = d0 + v * dur


def build_track():
    segs = []
    t, d = T_GREEN, GRID
    segs.append(Seg("LAUNCH", "straight", t, 1.0, -GRID, d, [], -1, False))
    t, d = segs[-1].t1, 0.0
    for lap in range(N_LAPS):
        for name, kind, dur, kmh, corners in LAP:
            if name == "S1" and lap in PIT_LAPS:
                v = TL_S1 / 2 / PIT_HALF_S
                for nm, du, vv in (("PIT_IN", PIT_HALF_S, v), ("PIT_STOP", PIT_STOP_S, 0.0),
                                   ("PIT_OUT", PIT_HALF_S, v)):
                    segs.append(Seg(nm, "pit", t, du, vv, d, [], lap, True))
                    t, d = segs[-1].t1, segs[-1].d1
                continue
            segs.append(Seg(name, kind, t, dur, kmh / 3.6, d, corners, lap, False))
            t, d = segs[-1].t1, segs[-1].d1
            if lap == N_LAPS - 1 and name == "S1":
                return segs
    return segs


TL_S1 = LAP[0][2] * LAP[0][3] / 3.6
SEGS = build_track()
T_END = SEGS[-1].t1


def seg_at(t):
    if t < T_GREEN:
        return None
    for s in SEGS:
        if s.t0 <= t < s.t1:
            return s
    return SEGS[-1]


def dist(t):
    s = seg_at(t)
    if s is None:
        return GRID
    return s.d0 + s.v * (min(t, s.t1) - s.t0)


def time_at(x):
    """누적 거리 x에 처음 닿는 시각."""
    for s in SEGS:
        if s.v > 0 and s.d0 <= x < s.d1 + 1e-9:
            return s.t0 + (x - s.d0) / s.v
    raise ValueError(x)


def tick(t):
    return int(round(t * HZ))


def case_impact_t(c):
    s = next(s for s in SEGS if s.lap == c["lap"] and s.name == c["seg"])
    t = tick(s.t0 + c["at"]) / HZ
    assert s.t0 < t < s.t1, c["name"]
    return t


IMPACTS = [(case_impact_t(c), c) for c in CASES]
PIT_ENTRIES = sorted(s.t0 for s in SEGS if s.name == "PIT_IN")


def damage_at(t):
    """t 시점의 손상 상태 — 가장 최근 충격(다음 피트 진입 전까지 유지)."""
    cur = None
    for ti, c in IMPACTS:
        if ti <= t:
            nxt = [p for p in PIT_ENTRIES if p > ti]
            if not nxt or t < nxt[0]:
                cur = (ti, c)
    return cur


def curvature(s, t, n):
    """구간 s의 시각 t 곡률(1/m) — 코너 반 사인 + 직선 흔들림 (틱 n 기반 결정적)."""
    wob = 4e-5 * math.sin(n * 0.23) + 3e-5 * math.sin(n * 1.13)
    if s.kind == "pit" or s.v == 0:
        return 0.0
    k = 0.0
    u = t - s.t0
    for c0, cl, peak in s.corners:
        if c0 <= u < c0 + cl:
            k = peak / GAIN * math.sin(math.pi * (u - c0) / cl)
    return k + wob


def phase_at(t):
    if t < T_FORMATION_END:
        return 3
    if t < T_GREEN:
        return 4
    return 5


def motion(n):
    """(속도 km/h, 조향, 요레이트, 횡속도, 피트레인) — 반올림은 telemetry.py와 같은 자리수."""
    t = n / HZ
    s = seg_at(t)
    if s is None or s.v == 0:
        return 0.0, 0.0, 0.0, 0.0, bool(s and s.pit)
    k = curvature(s, t, n)
    dmg = damage_at(t)
    offset = dmg[1]["offset"] if dmg else 0.0
    steer = offset + GAIN * k + 0.0015 * math.sin(n * 1.7) + 0.0008 * math.sin(n * 0.37)
    yaw = s.v * k
    lat = SLIP * s.v * s.v * k + 0.05 * math.sin(n * 0.71)
    return (round(s.v * 3.6, 1), round(steer, 4), round(yaw, 4), round(lat, 2), s.pit)


def snapshot(n):
    t = round(n / HZ, 3)
    d = dist(t)
    laps = max(int(math.floor(d / TL)), 0) if d >= 0 else 0
    crossings = [time_at(k * TL) for k in range(0, laps + 1)] if d >= 0 else []
    lse = crossings[-1] if crossings else 0.0
    times = [crossings[k] - crossings[k - 1] for k in range(1, len(crossings))]
    last = times[-1] if times else 0.0
    ld = d % TL if d >= 0 else d + TL
    speed, steer, yaw, lat, pit = motion(n)
    dmg = damage_at(t)
    flat = bool(dmg and dmg[1].get("flat"))
    pitstops = sum(1 for p in SEGS if p.name == "PIT_OUT" and p.t1 <= t)
    laps_frac = max(d, 0.0) / TL

    def wheel(i):
        bad = flat and i == 1
        return {"brake_temp": 350.0, "pressure": 60.0 if bad else 172.0,
                "temps": [85.0, 88.0, 91.0], "carcass_temp": 85.0,
                "wear": round(max(1.0 - 0.008 * laps_frac, 0.0), 4),
                "flat": bad, "detached": False}

    last_imp = [(ti, c) for ti, c in IMPACTS if ti <= t]
    moving = speed > 0
    return {
        "t": t,
        "connected": True,
        "in_session": True,
        "session": {
            "track": "Circuit de Test",
            "session_type": 10,
            "current_et": t,
            "end_et": END_ET,
            "max_laps": INT_MAX,
            "track_len": round(TL, 2),
            "game_phase": phase_at(t),
            "yellow_state": 0,
            "sector_flags": [0, 0, 0],
            "pit_speed_limit": 80.0,
            "in_realtime": True,
            "raining": 0.0, "dark_cloud": 0.1,
            "ambient_temp": 22.0, "track_temp": 31.0,
            "avg_wetness": 0.0,
            "num_vehicles": 1,
        },
        "player": {
            "id": 0,
            "lap_number": laps + 1,
            "lap_start_et": round(lse, 3),
            "speed_kmh": speed,
            "rpm": 7200 if moving else 3000,
            "max_rpm": 9250,
            "gear": 5 if speed >= 150 else (3 if moving else 1),
            "fuel": round(max(70.0 - 1.2 * laps_frac, 0.0), 2),
            "fuel_capacity": 90.0,
            "water_temp": 88.0, "oil_temp": 102.0,
            "overheating": False, "detached": False,
            "front_wing_height": 0.0,
            "dent_severity": [0, 1, 0, 0, 0, 0, 0, 0] if dmg else [0] * 8,
            "last_impact_et": last_imp[-1][0] if last_imp else 0.0,
            "last_impact_mag": IMPACT_MAG if last_imp else 0.0,
            "in_pitlane": pit, "speed_limiter": pit,
            "steering": steer, "yaw_rate": yaw, "lat_vel": lat,
            "wheels": [wheel(i) for i in range(4)],
        },
        "vehicles": [{
            "id": 0, "driver": "나", "vehicle": "Porsche 963", "cls": "Hypercar",
            "is_player": True, "place": 1,
            "total_laps": laps,
            "lap_dist": round(ld, 1),
            "path_lat": 0.0,
            "sector": 1 + int(ld / TL * 3) % 3,
            "last_lap": round(last, 3),
            "best_lap": round(min(times), 3) if times else 0.0,
            "last_s1": round(last / 3, 3) if last else 0.0,
            "last_s2": round(last * 2 / 3, 3) if last else 0.0,
            "time_behind_next": 0.0, "laps_behind_next": 0, "time_behind_leader": 0.0,
            "in_pits": pit, "pit_state": 3 if pit else 0,
            "num_pitstops": pitstops,
            "num_penalties": 0, "finish_status": 0,
            "flag_blue": False,
            "estimated_lap": LAP_S,
            "time_into_lap": round(t - lse, 3),
            "lap_start_et": round(lse, 3),
            "pos": [round(math.cos(ld / TL * 2 * math.pi) * 700, 1), 0.0,
                    round(math.sin(ld / TL * 2 * math.pi) * 700, 1)],
            "in_garage": False,
        }],
    }


def median(xs):
    return sorted(xs)[len(xs) // 2]


def judge(snaps):
    """
    규칙대로 경우마다 기준·판정을 구한다 (기대값). 샘플 분류는 경계에서 멀어야 한다 —
    직선 샘플은 한계의 2/3 안, 코너 샘플은 한계의 1.5배 밖 (생성 모델이 판정 경계에 걸쳐
    엔진 반올림 차이로 갈리지 않게).
    """
    out = {}
    pre, post, base, no_base, cur = [], None, None, False, None
    ema, old_base, old_n, old_call = None, None, 0, {}
    pit_prev = False
    for s in snaps:
        p, t = s["player"], s["t"]
        if p["in_pitlane"] and not pit_prev:
            pre, post, base, no_base, cur = [], None, None, False, None
            ema, old_base, old_n = None, None, 0
        pit_prev = p["in_pitlane"]
        v = p["speed_kmh"]
        acc = abs(p["yaw_rate"]) * v / 3.6
        ok = (not p["in_pitlane"] and v >= SPEED_MIN and abs(p["steering"]) <= STEER_MAX
              and acc <= LAT_ACC_MAX and abs(p["lat_vel"]) <= LAT_VEL_MAX)
        if v >= SPEED_MIN and not p["in_pitlane"]:
            if ok:
                assert acc <= LAT_ACC_MAX * 2 / 3 and abs(p["lat_vel"]) <= LAT_VEL_MAX * 2 / 3, t
            else:
                assert acc >= LAT_ACC_MAX * 1.5, (t, acc)
            # 예전 감지 (EMA, |조향| ≤ 0.20 전부)
            if abs(p["steering"]) <= STEER_MAX:
                ema = p["steering"] if ema is None else (1 - OLD_ALPHA) * ema + OLD_ALPHA * p["steering"]
                if old_base is not None:
                    old_n += 1
                    if old_n >= OLD_JUDGE and cur and cur["name"] not in old_call \
                            and abs(ema - old_base) >= SHIFT_MIN:
                        old_call[cur["name"]] = (t, ema - old_base)
        if ok:
            if base is None and not no_base:
                pre = (pre + [p["steering"]])[-BASE_N:]
            elif post is not None:
                post = (post + [p["steering"]])[-JUDGE_N:]
                if len(post) == JUDGE_N and cur["name"] not in out:
                    shift = median(post) - base
                    out[cur["name"]] = dict(t=t, base=base, post=median(post), shift=shift,
                                            n_base=n_base)
                    post = None
        hit = next((c for ti, c in IMPACTS if abs(ti - t) < 1e-9), None)
        if hit is not None:
            cur = hit
            if old_base is None:
                old_base, old_n = ema, 0
            if base is None and not no_base:
                if len(pre) >= BASE_MIN:
                    base, n_base = median(pre), len(pre)
                else:
                    no_base = True
                    out[hit["name"]] = dict(t=t, base=None, n_pre=len(pre))
            if not no_base:
                post = []
    return out, old_call


def build():
    snaps = [snapshot(n) for n in range(tick(T_END) + 1)]
    res, old_call = judge(snaps)
    events, cases = [], []
    for ti, c in IMPACTS:
        r = res.get(c["name"])
        assert r is not None, f"{c['name']}: 판정 버퍼가 차지 않음"
        if c["name"] == "no_baseline":
            assert r["base"] is None and r["n_pre"] < BASE_MIN, c["name"]
            decision = None
        else:
            assert r["n_base"] >= BASE_MIN, c["name"]
            sh = abs(r["shift"])
            decision = "severe" if sh >= SHIFT_SEVERE else "mild" if sh >= SHIFT_MIN else None
            # 판정 시각이 점검 리포트(+8초) 쿨다운(20초) 밖 — 재시도 경계에 걸치지 않게
            assert r["t"] >= ti + REPORT_DELAY + REPORT_COOLDOWN + 1.0, c["name"]
            # 판정 시각에 아직 스위퍼(F)를 지나 S3 — 같은 랩 안
            seg = seg_at(r["t"])
            assert seg.lap == c["lap"] and seg.name == "S3", (c["name"], seg.name)
        if c.get("flat"):
            assert decision == "mild", "flat_tyre_pull: 펑크 규칙만 없으면 콜이 나갈 모양이어야 한다"
            decision = None
        elif c["name"] == "no_baseline":
            assert c["offset"] >= SHIFT_MIN, "no_baseline: 기준만 있으면 콜이 나갈 손상이어야 한다"
        assert decision == c["expect"], (c["name"], decision, r)
        if c["name"] == "healthy_sweepers":
            # 예전 감지라면 이 경우에서 콜 — 이 리플레이가 회귀를 잡는다는 근거
            assert c["name"] in old_call, "healthy_sweepers: 예전 EMA 감지가 콜하는 모양이 아님"
        if decision is not None:
            events.append({
                "t": round(r["t"], 2),
                "type": "damage_report",
                "data": {},
                "message": SAY_SEVERE if decision == "severe" else SAY_MILD,
            })
        case = {"name": c["name"], "impact_t": ti, "call": decision is not None, "note": c["note"]}
        if r.get("base") is not None:
            case.update(base=round(r["base"], 4), post=round(r["post"], 4),
                        shift=round(r["shift"], 4), judge_t=r["t"])
        if c["name"] in old_call:
            case["old_call_t"], case["old_shift"] = old_call[c["name"]][0], round(old_call[c["name"]][1], 4)
        cases.append(case)

    sidecar = {
        "scenario": NAME,
        "note": "얼라인 콜 — 곧게 가는 고속 샘플(요레이트×속도 ≤ 1.5m/s², 횡속도 ≤ 1m/s)의 조향 중앙값을 "
                "첫 충격 전(101개, 최소 25) ↔ 충격 뒤(51개가 찼을 때) 비교, 0.03 경미 · 0.08 심각. "
                "스위퍼 조향은 섞이지 않고, 기준 부족·타이어 펑크면 콜 없음",
        "types": ["damage_report"],
        "keys": ["align_damage", "align_severe"],
        "events": events,
        "cases": cases,
    }
    return snaps, sidecar


def write(out_dir, snaps, sidecar):
    body = "".join(json.dumps(s, ensure_ascii=False, separators=(",", ":")) + "\n"
                   for s in snaps).encode("utf-8")
    gz = os.path.join(out_dir, NAME + ".jsonl.gz")
    with open(gz, "wb") as raw:
        # mtime=0·빈 파일명 — 같은 입력이면 같은 바이트 (zlib 버전 차이는 --check가 해제 후 비교)
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
            f.write(body)
    side = os.path.join(out_dir, NAME + ".expect.json")
    with open(side, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(sidecar, indent=1, sort_keys=True, ensure_ascii=False) + "\n")
    return gz, side


def read_gz(path):
    with gzip.open(path, "rb") as f:
        return f.read()


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=os.path.join(root, "data", "replays"))
    ap.add_argument("--check", action="store_true",
                    help="임시 폴더에 다시 만들어 저장소 파일과 대조 (압축 해제 바이트)")
    args = ap.parse_args()
    snaps, sidecar = build()
    if not args.check:
        os.makedirs(args.out_dir, exist_ok=True)
        gz, side = write(args.out_dir, snaps, sidecar)
        print(f"{gz}: 스냅샷 {len(snaps)}개 (+ {os.path.basename(side)}, "
              f"기대 콜 {len(sidecar['events'])}개 / 경우 {len(sidecar['cases'])}개)")
        return 0
    with tempfile.TemporaryDirectory() as tmp:
        gz, side = write(tmp, snaps, sidecar)
        mine_gz = os.path.join(args.out_dir, NAME + ".jsonl.gz")
        mine_side = os.path.join(args.out_dir, NAME + ".expect.json")
        if not (os.path.exists(mine_gz) and os.path.exists(mine_side)):
            print(f"❌ {NAME}: 파일 없음 — 재생성 필요 (python3 tools/make_align_replay.py)")
            return 1
        with open(mine_side, "rb") as a, open(side, "rb") as b:
            same = read_gz(mine_gz) == read_gz(gz) and a.read() == b.read()
    if same:
        print(f"✅ {NAME}: 생성기 출력과 동일 (스냅샷 {len(snaps)}개)")
        return 0
    print(f"❌ {NAME}: 생성기 출력과 다름 — 재생성 필요 (python3 tools/make_align_replay.py)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
