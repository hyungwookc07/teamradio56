"""
경쟁자 피트 콜 회귀용 합성 리플레이 생성기 — 리플레이 + 기대 콜(expect) 사이드카.

사용자 제보: "앞차가 피트인만 하면 '언더컷 어탬트'라고 함, 롤링 스타트 대기 중에도".
녹화 리플레이(race_full 등)에는 그린 중 앞차 피트가 한 번뿐이고 그리드·포메이션 피트,
처음 볼 때 이미 피트에 있는 차, 순위 0, 진입 틱 순위 흔들림, 랩 다운 차, FCY 피트가 없어서
analyzers/rivals.py(C# RivalAnalyzer)의 이런 경로가 한 번도 재생되지 않았다.
여기서는 한 레이스에 그 경우를 전부 넣고, 어떤 콜이 몇 초에 나와야 하는지를
data/replays/rival_pit.expect.json에 같이 쓴다 (tools/expect_calls.py가 파이썬·C# 둘 다 대조).

규칙 (기대값의 근거):
  · 콜은 그린/FCY(페이즈 5·6)에서만 — 그리드·포메이션·카운트다운(0~4)은 무음
  · 처음 보는 차(첫 레이스 틱, 틱 공백 뒤)는 기준값만 — 이미 피트에 있던 차는 콜하지 않는다
  · 앞/뒤와 클래스 순위는 진입 직전 틱 기준 — 진입 틱에 순위가 바뀌어도 앞차는 앞차
  · 순위 0(내 차든 상대든)이면 침묵, 차고로 순간이동(in_garage)도 침묵
  · 뒤차 "언더컷" 콜은 그린 + 상대 1랩 이상 완료 + 트랙 위에서 1/4랩 안쪽 뒤일 때만
    (FCY 피트·첫 랩 피트·랩 다운 차는 침묵 — 음성 팩에 "뒤차 + 갭 안내" 문장이 없다)
  · 같은 종류 쿨다운 90초 — 기대 콜끼리는 90초 넘게 띄운다

모델: 단일 클래스 5대, 같은 페이스(80초/랩)라 순서는 스크립트한 정지(halt)로만 바뀐다.
그린 전에는 그리드에 서 있다. 피트 정지는 기본으로 시간 손실 없음(순서 고정 — 경우마다
규칙 하나만 보이게), 랩 다운을 만드는 정지만 그 시간 동안 멈춘다. 위치는 시각에서
해석적으로 구한다 (dt 누적 없음 — 재생성 결과가 머신과 무관하게 같다).

사용법: python tools/make_rival_pit_replay.py [--out-dir data/replays] [--check]
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

NAME = "rival_pit"
TL = 2400.0                 # 트랙 길이 (m)
HZ = 5
LAP_S = 80.0
V = TL / LAP_S              # m/s
INT_MAX = 2147483647
END_ET = 1500.0             # 시간제 25분 — 리플레이(820초) 안에 남은 시간 안내가 없다
GT3 = "LMGT3"

T_FORMATION_END = 40.0      # 0~40 페이즈 3 (포메이션/롤링 스타트 대기)
T_GREEN = 60.0              # 40~60 페이즈 4 (카운트다운), 60부터 그린
T_FCY = 470.0               # FCY (페이즈 6) — 피트 닫힘
T_FCY_OPEN = 500.0          # FCY 중 피트 오픈
T_RESTART = 590.0           # 그린 복귀
T_END = 820.0
GAP = (600.0, 610.0)        # 이 사이(양끝 제외) 스냅샷 없음 — 모니터에 다녀온 공백

PLAYER = 0
# (id, 드라이버, 차, 그리드 위치 m — 음수 = 라인 뒤)
CARS = [
    (1, "리바이", "Ferrari 296 GT3", -40.0),
    (2, "헌터", "McLaren 720S GT3", -200.0),
    (PLAYER, "나", "Porsche 911 GT3 R", -360.0),
    (3, "로키", "Aston Vantage GT3", -520.0),
    (4, "토르", "BMW M4 GT3", -680.0),
]

SAY_AHEAD = "P{p} in class, car ahead, just pitted. I'll call the gap when he's out."
SAY_BEHIND = ("P{p} in class, car behind, just pitted. "
              "Undercut attempt. I'll look at our timing.")

# 경우 하나 = 피트 진입 하나. expect: None(침묵) 또는 (rel, 진입 직전 클래스 순위).
#   halt  — 피트에 있는 동안 멈춤 (시간 손실)     garage — 차고 순간이동 (끝까지 차고)
#   zero  — [(차 id, 시작, 끝)] 그 구간 순위 0     swap — (id_a, id_b, 시작, 끝) 순위 맞바꿈
CASES = [
    dict(name="pitlane_start", car=4, t_in=0.0, t_out=30.0, expect=None,
         note="포메이션 — 첫 레이스 틱에 이미 피트에 있는 차 (처음 봄 = 기준값만)"),
    dict(name="formation_ahead", car=2, t_in=10.0, t_out=12.0, expect=None,
         note="포메이션(롤링 스타트 대기) 중 앞차 피트 플래그"),
    dict(name="grid_place0", car=3, t_in=20.0, t_out=22.0, zero=[(PLAYER, 15.0, 25.0)],
         expect=None, note="포메이션 중 내 순위 0 + 뒤차 피트 (예전: 모두 뒤차 → 언더컷)"),
    dict(name="countdown", car=3, t_in=45.0, t_out=47.0, expect=None,
         note="카운트다운(페이즈 4) 중 뒤차 피트 플래그"),
    dict(name="lap1_behind", car=3, t_in=100.0, t_out=106.0, expect=None,
         note="그린 첫 랩에 뒤차 피트 — 언더컷 아님"),
    dict(name="lap1_long_stop", car=4, t_in=160.0, t_out=227.0, halt=True, expect=None,
         note="첫 랩 긴 정지(67초) — 이 차는 이후 한 랩 다운"),
    dict(name="ahead_green", car=1, t_in=200.0, t_out=206.0, expect=("ahead", 1),
         note="그린 중 앞차 피트 → 갭 안내 콜 하나"),
    dict(name="behind_undercut", car=3, t_in=300.0, t_out=306.0, expect=("behind", 4),
         note="그린 중 바로 뒤(160m) 차 피트 → 언더컷 콜 하나"),
    dict(name="ahead_reshuffle", car=2, t_in=400.0, t_out=406.0, swap=(2, PLAYER, 400.0, 401.0),
         expect=("ahead", 2),
         note="앞차 피트 진입 틱에 순위가 나와 맞바뀜 — 그래도 앞차 콜 (예전: 뒤차 언더컷)"),
    dict(name="fcy_behind", car=3, t_in=505.0, t_out=511.0, expect=None,
         note="FCY(피트 오픈) 중 뒤차 피트 — 언더컷 아님"),
    dict(name="fcy_ahead", car=1, t_in=520.0, t_out=526.0, expect=("ahead", 1),
         note="FCY 중 앞차 피트 → 갭 안내 콜 (앞 경우가 쿨다운을 먹지 않는다)"),
    dict(name="monitor_gap", car=3, t_in=605.0, t_out=615.0, expect=None,
         note="틱 공백(600~610) 사이에 피트 인 — 돌아왔을 때 이미 피트 (기준값 다시)"),
    dict(name="lapped_on_road", car=4, t_in=650.0, t_out=656.0, expect=None,
         note="한 랩 다운 차가 트랙 위 70m 앞에서 피트 — 클래스 뒤지만 언더컷 아님"),
    dict(name="player_place0", car=2, t_in=703.0, t_out=708.0, zero=[(PLAYER, 700.0, 706.0)],
         expect=None, note="그린 중 내 순위 0 구간에 앞차 피트"),
    dict(name="rival_place0", car=3, t_in=763.0, t_out=768.0, zero=[(3, 760.0, 766.0)],
         expect=None, note="그린 중 상대 순위 0 구간에 피트 (예전: 클래스 P1 앞차로 콜)"),
    dict(name="garage_teleport", car=2, t_in=800.0, t_out=None, garage=True, halt=True,
         expect=None, note="차고로 순간이동 (in_pits + in_garage 같은 틱)"),
]


def tick(t):
    return int(round(t * HZ))


def phase_at(t):
    if t < T_FORMATION_END:
        return 3, 0
    if t < T_GREEN:
        return 4, 0
    if T_FCY <= t < T_RESTART:
        return 6, (4 if t >= T_FCY_OPEN else 2)
    return 5, 0


class Car:
    def __init__(self, cid, driver, vehicle, offset):
        self.id = cid
        self.driver = driver
        self.vehicle = vehicle
        self.offset = offset
        self.is_player = cid == PLAYER
        self.stops = sorted((c for c in CASES if c["car"] == cid), key=lambda c: c["t_in"])
        self.halts = [(max(c["t_in"], T_GREEN), c["t_out"] if c["t_out"] is not None else math.inf)
                      for c in self.stops if c.get("halt")]

    def moving(self, t):
        """그린 이후 실제로 달린 시간 (멈춘 구간 제외)."""
        m = max(t - T_GREEN, 0.0)
        for a, b in self.halts:
            if t > a:
                m -= min(t, b) - a
        return m

    def dist(self, t):
        return self.offset + V * self.moving(t)

    def time_at(self, x):
        """누적 거리 x에 처음 닿는 시각 (그린 이후)."""
        need = (x - self.offset) / V
        t = T_GREEN + need
        for a, b in self.halts:
            if t > a:
                t += b - a
        return t

    def stop_at(self, t):
        for c in self.stops:
            end = c["t_out"] if c["t_out"] is not None else math.inf
            if c["t_in"] <= t < end:
                return c
        return None

    def pitstops_done(self, t):
        return sum(1 for c in self.stops
                   if c["t_in"] >= T_GREEN and c["t_out"] is not None and c["t_out"] <= t)


def overrides(t):
    zero, swaps = set(), []
    for c in CASES:
        for cid, a, b in c.get("zero", []):
            if a <= t < b:
                zero.add(cid)
        if c.get("swap"):
            ia, ib, a, b = c["swap"]
            if a <= t < b:
                swaps.append((ia, ib))
    return zero, swaps


def snapshot(n, cars):
    t = round(n / HZ, 3)
    phase, yellow = phase_at(t)
    rows = []
    for c in cars:
        d = c.dist(t)
        laps = max(int(math.floor(d / TL)), 0) if d >= 0 else 0
        crossings = [c.time_at(k * TL) for k in range(0, laps + 1)] if d >= 0 else []
        lse = crossings[-1] if crossings else 0.0
        times = [crossings[k] - crossings[k - 1] for k in range(1, len(crossings))]
        rows.append((c, d, laps, lse, times))
    ranked = sorted(rows, key=lambda r: (-r[1], r[0].id))
    place = {r[0].id: i + 1 for i, r in enumerate(ranked)}
    zero, swaps = overrides(t)
    for ia, ib in swaps:
        place[ia], place[ib] = place[ib], place[ia]
    for cid in zero:
        place[cid] = 0
    lead_d = ranked[0][1]
    pd = next(r[1] for r in rows if r[0].is_player)

    vehicles = []
    for c, d, laps, lse, times in rows:
        i = next(k for k, r in enumerate(ranked) if r[0] is c)
        tbn = max(ranked[i - 1][1] - d, 0.0) / V if i > 0 else 0.0
        lbn = int(math.floor((ranked[i - 1][1] - d) / TL)) if i > 0 else 0
        tbl = max(lead_d - d, 0.0) / V
        ld = d % TL
        if c.is_player:
            plat = 0.0
        else:
            gap = (d - pd + TL / 2) % TL - TL / 2
            plat = (2.8 if c.id % 2 else -2.8) if abs(gap) < 40.0 else ((c.id * 37) % 10) / 10 - 0.5
        stop = c.stop_at(t)
        last = times[-1] if times else 0.0
        vehicles.append({
            "id": c.id, "driver": c.driver, "vehicle": c.vehicle, "cls": GT3,
            "is_player": c.is_player, "place": place[c.id],
            "total_laps": laps,
            "lap_dist": round(ld, 1),
            "path_lat": round(plat, 2),
            "sector": 1 + int(ld / TL * 3) % 3,
            "last_lap": round(last, 3),
            "best_lap": round(min(times), 3) if times else 0.0,
            "last_s1": round(last / 3, 3) if last else 0.0,
            "last_s2": round(last * 2 / 3, 3) if last else 0.0,
            "time_behind_next": round(tbn, 3),
            "laps_behind_next": max(lbn, 0),
            "time_behind_leader": round(tbl, 3),
            "in_pits": stop is not None, "pit_state": 3 if stop is not None else 0,
            "num_pitstops": c.pitstops_done(t),
            "num_penalties": 0, "finish_status": 0,
            "flag_blue": False,
            "estimated_lap": LAP_S,
            "time_into_lap": round(t - lse, 3),
            "lap_start_et": round(lse, 3),
            "pos": [round(math.cos(ld / TL * 2 * math.pi) * 600, 1), 0.0,
                    round(math.sin(ld / TL * 2 * math.pi) * 600, 1)],
            "in_garage": bool(stop is not None and stop.get("garage")),
        })

    _, pdist, plaps, plse, _ = next(r for r in rows if r[0].is_player)
    laps_frac = max(pdist, 0.0) / TL
    wear = round(max(1.0 - 0.008 * laps_frac, 0.0), 4)

    def wheel():
        return {"brake_temp": 350.0, "pressure": 172.0, "temps": [85.0, 88.0, 91.0],
                "carcass_temp": 85.0, "wear": wear, "flat": False, "detached": False}

    moving = phase in (5, 6)
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
            "track_len": TL,
            "game_phase": phase,
            "yellow_state": yellow,
            "sector_flags": [0, 0, 0],
            "pit_speed_limit": 80.0,
            "in_realtime": True,
            "raining": 0.0, "dark_cloud": 0.1,
            "ambient_temp": 22.0, "track_temp": 31.0,
            "avg_wetness": 0.0,
            "num_vehicles": len(cars),
        },
        "player": {
            "id": PLAYER,
            "lap_number": plaps + 1,
            "lap_start_et": round(plse, 3),
            "speed_kmh": round(V * 3.6, 1) if moving else 0.0,
            "rpm": 7200 if moving else 3000,
            "max_rpm": 9250,
            "gear": 4 if moving else 1,
            "fuel": round(max(70.0 - 2.5 * laps_frac, 0.0), 2),
            "fuel_capacity": 90.0,
            "water_temp": 88.0, "oil_temp": 102.0,
            "overheating": False, "detached": False,
            "dent_severity": [0] * 8,
            "last_impact_et": 0.0,
            "last_impact_mag": 0.0,
            "in_pitlane": False, "speed_limiter": False,
            "wheels": [wheel(), wheel(), wheel(), wheel()],
        },
        "vehicles": vehicles,
    }


def class_place(snap, cid):
    me = next(v for v in snap["vehicles"] if v["id"] == cid)
    return 1 + sum(1 for v in snap["vehicles"] if 0 < v["place"] < me["place"])


def progress(snap, cid):
    v = next(v for v in snap["vehicles"] if v["id"] == cid)
    return v["total_laps"] + v["lap_dist"] / TL


def build():
    cars = [Car(*c) for c in CARS]
    snaps = []
    for n in range(tick(T_END) + 1):
        t = n / HZ
        if GAP[0] < t < GAP[1]:
            continue
        snaps.append(snapshot(n, cars))
    by_t = {tick(s["t"]): s for s in snaps}

    # 기대값이 스크립트 의도와 맞는지 생성 시점에 확인 (진입 직전 틱 기준)
    events, cases = [], []
    last_call = -math.inf
    for c in CASES:
        n = tick(c["t_in"])
        assert abs(n / HZ - c["t_in"]) < 1e-9, f"{c['name']}: 진입 시각이 틱에 안 맞음"
        now = by_t.get(n)
        before = by_t.get(n - 1)
        phase = phase_at(c["t_in"])[0]
        car = next(k for k in cars if k.id == c["car"])
        if c["name"] == "monitor_gap":
            back = by_t[tick(GAP[1])]
            assert now is None and next(v for v in back["vehicles"] if v["id"] == c["car"])["in_pits"], \
                "monitor_gap: 공백 중에 들어가 공백 뒤 첫 틱에 피트에 있어야 한다"
        exp = c["expect"]
        if exp is not None:
            rel, p = exp
            assert phase in (5, 6) and before is not None
            assert class_place(before, c["car"]) == p, f"{c['name']}: 클래스 순위 {p} 아님"
            mine = class_place(before, PLAYER)
            assert (p < mine) == (rel == "ahead") and 0 < abs(p - mine) <= 2, c["name"]
            if rel == "behind":
                lag = progress(before, PLAYER) - progress(before, c["car"])
                assert phase == 5 and progress(before, c["car"]) >= 1.0 and 0 < lag < 0.25, c["name"]
            assert c["t_in"] - last_call >= 90.0, f"{c['name']}: 앞 콜과 90초 쿨다운 안"
            last_call = c["t_in"]
            events.append({
                "t": round(c["t_in"], 2),
                "type": "rival_pit",
                "data": {"driver": car.driver, "their_class_place": p,
                         "rel": "앞" if rel == "ahead" else "뒤",
                         "undercut_risk": rel == "behind"},
                "say": (SAY_AHEAD if rel == "ahead" else SAY_BEHIND).format(p=p),
            })
        elif c["name"] == "lapped_on_road":
            lag = progress(before, PLAYER) - progress(before, c["car"])
            assert 0.9 < lag < 1.0 and class_place(before, c["car"]) - class_place(before, PLAYER) == 2
        cases.append({"name": c["name"], "t": round(c["t_in"], 2), "car": car.driver,
                      "phase": phase, "call": exp is not None, "note": c["note"]})

    sidecar = {
        "scenario": NAME,
        "note": "경쟁자 피트 콜 — 그린/FCY에서만, 처음 보는 차·순위 0·차고 순간이동은 침묵, "
                "앞/뒤는 진입 직전 순위, 뒤차 언더컷은 그린·1랩 이후·1/4랩 안쪽만",
        "types": ["rival_pit"],
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
            print(f"❌ {NAME}: 파일 없음 — 재생성 필요 (python3 tools/make_rival_pit_replay.py)")
            return 1
        with open(mine_side, "rb") as a, open(side, "rb") as b:
            same = read_gz(mine_gz) == read_gz(gz) and a.read() == b.read()
    if same:
        print(f"✅ {NAME}: 생성기 출력과 동일 (스냅샷 {len(snaps)}개)")
        return 0
    print(f"❌ {NAME}: 생성기 출력과 다름 — 재생성 필요 (python3 tools/make_rival_pit_replay.py)")
    return 1


if __name__ == "__main__":
    sys.exit(main())
