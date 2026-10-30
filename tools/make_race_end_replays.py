"""
레이스 종료(체커) 회귀용 합성 리플레이 생성기 — 리플레이 + 정답(truth) 사이드카.

기존 리플레이(race_full, traffic2)는 레이스 중간에 끝나서 마지막 랩·체커 판단이 한 번도
재생되지 않았다. 여기서는 짧은 레이스 5개를 체커 이후까지 만들고, 규칙대로 계산한 정답을
data/replays/<이름>.truth.json에 같이 쓴다 (tools/race_end_oracle.py가 대조).
재시작 시나리오는 같은 레이스를 ET만 되감아 한 번 더 돌린다 (스냅샷 t는 계속 증가 —
라이브의 벽시계처럼). 정답은 레이스마다 "races" 목록에 들어간다.

규칙 (rF2/LMU): 시간제는 시계 0 이후 전체 1위가 라인을 넘을 때 체커, 다른 차는 그 뒤 각자
첫 라인 통과에서 피니시. 랩제는 1위가 max_laps를 채울 때. 2번째 통과 상한은 두지 않는다.

차량 위치는 라인 통과 시각에서 해석적으로 구한다 (dt 누적 없음) — 정답과 리플레이가 같은
식에서 나오고, 재생성 결과가 머신과 무관하게 같다 (--check는 압축 해제 바이트로 비교).

사용법: python tools/make_race_end_replays.py [--out-dir data/replays] [--only NAME] [--check]
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
import io
import json
import math
import os
import tempfile

TL = 2400.0                 # 트랙 길이 (m)
HZ = 5
INT_MAX = 2147483647
LAP_LIMIT_MAX = 100000
GT3 = "LMGT3"
HY = "Hypercar"


class Car:
    def __init__(self, cid, driver, vehicle, cls, offset, lap_s, overrides=None,
                 retire_t=None, is_player=False):
        self.id = cid
        self.driver = driver
        self.vehicle = vehicle
        self.cls = cls
        self.offset = offset            # t=0 위치 (m), 음수 = 라인 뒤
        self.lap_s = lap_s
        self.over = overrides or {}     # 랩 번호 → 그 랩 랩타임
        self.retire_t = retire_t        # 이 시각에 트랙 위에서 멈춤 (DNF)
        self.is_player = is_player
        self.k0 = int(math.floor(offset / TL)) + 1
        self.cross = []                 # [(t, k)] — k ≤ 0은 스타트 라인 통과 (랩 아님)

    def L(self, k):
        return self.over.get(k, self.lap_s)

    def build(self, horizon):
        k = self.k0
        t = (k * TL - self.offset) / TL * self.L(k)
        out = []
        while t <= horizon:
            if self.retire_t is not None and t > self.retire_t:
                break
            out.append((t, k))
            k += 1
            t = t + self.L(k)
        self.cross = out

    def dist(self, t):
        if self.retire_t is not None:
            t = min(t, self.retire_t)
        prev_t, prev_k = None, self.k0 - 1
        for (tk, k) in self.cross:
            if tk > t:
                if prev_t is None:
                    return self.offset + (t / tk) * (k * TL - self.offset)
                return prev_k * TL + (t - prev_t) / self.L(k) * TL
            prev_t, prev_k = tk, k
        if prev_t is None:
            return self.offset + t / self.L(self.k0) * TL
        return prev_k * TL + (t - prev_t) / self.L(prev_k + 1) * TL


def tick_of(t):
    """그 시각의 라인 통과가 처음 보이는 틱 시각."""
    n = int(math.ceil(t * HZ - 1e-9))
    if n / HZ < t:
        n += 1
    return round(n / HZ, 3)


def truth(cars, E, M):
    timed = E > 0
    lapr = 0 < M < LAP_LIMIT_MAX
    ev = sorted((t, c.id, k) for c in cars for (t, k) in c.cross if k >= 1)
    byid = {c.id: c for c in cars}
    flag = None
    flag_car = None
    fin = {}
    leader_cross = []
    for (t, cid, k) in ev:
        if cid in fin:
            continue
        if flag is not None and t >= flag:
            fin[cid] = (t, k)
            continue
        running = [c for c in cars if c.id not in fin
                   and (c.retire_t is None or c.retire_t > t)]
        leader = max(running, key=lambda c: (c.dist(t - 1e-6), -c.id))
        if leader.id == cid:
            leader_cross.append(t)
            if (timed and t >= E) or (lapr and k >= M):
                flag, flag_car = t, cid
                fin[cid] = (t, k)
    assert flag is not None, "체커가 안 나왔다 — horizon 부족"
    return flag, flag_car, fin, leader_cross, byid


def snapshot(n, cars, player, E, M, flag, fin):
    et = round(n / HZ, 3)
    phase = 4 if et < 3.0 else (8 if et >= flag else 5)
    rows = []
    for c in cars:
        f = fin.get(c.id)
        done = [(t, k) for (t, k) in c.cross if t <= et and (f is None or t <= f[0])]
        laps = max(done[-1][1], 0) if done else 0
        lse = done[-1][0] if done else 0.0
        lasts = []
        for i, (tk, k) in enumerate(done):
            if k < 1:
                continue
            lasts.append(tk - done[i - 1][0] if i >= 1 else c.L(c.k0))
        last = lasts[-1] if lasts else 0.0
        best = min(lasts) if lasts else 0.0
        d = c.dist(et)
        fs = 0
        if f is not None and et >= f[0]:
            fs = 1
        if c.retire_t is not None and et >= c.retire_t:
            fs = 2
        rank_d = d if fs != 1 else laps * TL
        rows.append((c, dict(laps=laps, lse=lse, last=last, best=best, d=d, fs=fs,
                             rank_d=rank_d, fin_t=f[0] if fs == 1 else 0.0)))
    ranked = sorted(rows, key=lambda r: (-r[1]["rank_d"], r[1]["fin_t"], r[0].id))
    place = {r[0].id: i + 1 for i, r in enumerate(ranked)}
    lead_d = ranked[0][1]["rank_d"]
    pd = next(r[1]["d"] for r in rows if r[0].is_player)

    vehicles = []
    for c, s in rows:
        speed = TL / c.lap_s
        p = place[c.id]
        tbn = 0.0
        if p > 1:
            tbn = max(ranked[p - 2][1]["rank_d"] - s["rank_d"], 0.0) / speed
        tbl = max(lead_d - s["rank_d"], 0.0) / speed
        ld = s["d"] % TL
        if c.is_player:
            plat = 0.0
        else:
            gap = (s["d"] - pd + TL / 2) % TL - TL / 2
            if abs(gap) < 40.0:
                plat = 2.8 if c.id % 2 else -2.8
            else:
                plat = ((c.id * 37) % 10) / 10 - 0.5
        last = s["last"]
        vehicles.append({
            "id": c.id, "driver": c.driver, "vehicle": c.vehicle, "cls": c.cls,
            "is_player": c.is_player, "place": p,
            "total_laps": s["laps"],
            "lap_dist": round(ld, 1),
            "path_lat": round(plat, 2),
            "sector": 1 + int(ld / TL * 3) % 3,
            "last_lap": round(last, 3),
            "best_lap": round(s["best"], 3),
            "last_s1": round(last / 3, 3) if last else 0.0,
            "last_s2": round(last * 2 / 3, 3) if last else 0.0,
            "time_behind_next": round(tbn, 3),
            "laps_behind_next": 0,
            "time_behind_leader": round(tbl, 3),
            "in_pits": False, "pit_state": 0,
            "num_pitstops": 0,
            "num_penalties": 0, "finish_status": s["fs"],
            "flag_blue": False,
            "estimated_lap": round(c.lap_s, 3),
            "time_into_lap": round(et - s["lse"], 3),
            "lap_start_et": round(s["lse"], 3),
            "pos": [round(math.cos(ld / TL * 2 * math.pi) * 600, 1), 0.0,
                    round(math.sin(ld / TL * 2 * math.pi) * 600, 1)],
            "in_garage": False,
        })

    ps = next(s for c, s in rows if c.is_player)
    laps_frac = pd / TL
    wear = round(max(1.0 - 0.008 * laps_frac, 0.0), 4)

    def wheel():
        return {"brake_temp": 350.0, "pressure": 172.0, "temps": [85.0, 88.0, 91.0],
                "carcass_temp": 85.0, "wear": wear, "flat": False, "detached": False}

    player_tele = {
        "id": player.id,
        "lap_number": ps["laps"] + 1,
        "lap_start_et": round(ps["lse"], 3),
        "speed_kmh": round(TL / player.lap_s * 3.6, 1),
        "rpm": 7200,
        "max_rpm": 9250,
        "gear": 4,
        "fuel": round(max(70.0 - 2.5 * laps_frac, 0.0), 2),
        "fuel_capacity": 90.0,
        "water_temp": 88.0, "oil_temp": 102.0,
        "overheating": False, "detached": False,
        "dent_severity": [0] * 8,
        "last_impact_et": 0.0,
        "last_impact_mag": 0.0,
        "in_pitlane": False, "speed_limiter": False,
        "wheels": [wheel(), wheel(), wheel(), wheel()],
    }
    return {
        "t": et,
        "connected": True,
        "in_session": True,
        "session": {
            "track": "Circuit de Test",
            "session_type": 10,
            "current_et": et,
            "end_et": E,
            "max_laps": M,
            "track_len": TL,
            "game_phase": phase,
            "yellow_state": 0,
            "sector_flags": [0, 0, 0],
            "pit_speed_limit": 80.0,
            "in_realtime": True,
            "raining": 0.0, "dark_cloud": 0.1,
            "ambient_temp": 22.0, "track_temp": 31.0,
            "avg_wetness": 0.0,
            "num_vehicles": len(cars),
        },
        "player": player_tele,
        "vehicles": vehicles,
    }


def me_car(cid=0, overrides=None):
    return Car(cid, "나", "Porsche 911 GT3 R", GT3, 0.0, 80.0, overrides=overrides,
               is_player=True)


def lead_cars():
    return [me_car(),
            Car(1, "리바이", "Ferrari 296 GT3", GT3, -160.0, 80.6),
            Car(2, "헌터", "McLaren 720S GT3", GT3, -330.0, 81.2)]


# (이름, 노트, E, M, 차량, final_calls 종류, trace_exact_from_lap)
SCENARIOS = [
    ("race_end_lead", "내가 전체 1위 — 시계 0 이후 내 라인 통과가 체커 (단일 클래스)",
     443.0, INT_MAX, lead_cars, "crossing", 1),
    ("race_end_lapped", "하이퍼카에 랩 다운 + 선두 DNF — 시계 0 직후 통과해도 리더 체커 전이면 한 랩 더",
     470.0, INT_MAX,
     lambda: [me_car(),
              Car(1, "토요타7", "Toyota GR010", HY, 2047.0, 65.0, retire_t=395.0),
              Car(2, "페라리50", "Ferrari 499P", HY, 1809.0, 69.0),
              Car(3, "헌터", "McLaren 720S GT3", GT3, -200.0, 80.5),
              Car(4, "리바이", "Ferrari 296 GT3", GT3, 300.0, 79.5)],
     "crossing", 1),
    ("race_end_laps", "랩제 7랩 — 리더가 7랩을 채우면 체커, 랩 다운인 나는 6랩에서 끝",
     0.0, 7,
     lambda: [me_car(),
              Car(1, "토요타7", "Toyota GR010", HY, 900.0, 68.0),
              Car(2, "헌터", "McLaren 720S GT3", GT3, -200.0, 80.5)],
     "crossing", 1),
    ("race_end_late", "예측(2랩) 빗나감 — 선두 DNF로 다른 리더가 일찍 체커, 랩 중간 안전망 '마지막 랩'",
     460.0, INT_MAX,
     lambda: [me_car(),
              Car(1, "토요타7", "Toyota GR010", HY, 1800.0, 68.0, retire_t=420.0),
              Car(2, "페라리50", "Ferrari 499P", HY, 353.0, 68.0),
              Car(3, "헌터", "McLaren 720S GT3", GT3, -200.0, 80.5),
              Car(4, "리바이", "Ferrari 296 GT3", GT3, 500.0, 79.0),
              Car(5, "로키", "Aston Vantage GT3", GT3, 250.0, 79.6)],
     "net", 6),
    ("race_end_recall", "예측(1랩) 빗나감 — 리더가 마지막 랩에 25초 잃어 체커가 늦음, 다음 통과에서 '마지막 랩' 다시",
     455.0, INT_MAX,
     lambda: [me_car(),
              Car(1, "토요타7", "Toyota GR010", HY, 388.2, 68.0, overrides={7: 93.0}),
              Car(2, "헌터", "McLaren 720S GT3", GT3, -200.0, 80.5)],
     "recall", 6),
    ("race_end_slowlead", "내가 전체 1위, 예측 2랩인데 마지막 랩에서 30초 잃어 시계가 랩 중간에 끝남 — "
     "리더 체커는 내 통과라 플래그가 없다, 시계 0 직후 랩 중간 '마지막 랩'",
     481.5, INT_MAX,
     # 5랩을 1초 느리게 — 그 페이스 콜의 쿨다운(120초)이 6랩의 "Lost 29.0"(코코로 세그먼트
     # 도메인 9.9초 밖)을 막는다. 예측은 그대로 2랩 (5랩 통과 401, 시계까지 80.5초)
     lambda: [me_car(overrides={5: 81.0, 6: 110.0}),
              Car(1, "리바이", "Ferrari 296 GT3", GT3, -600.0, 86.0),
              Car(2, "헌터", "McLaren 720S GT3", GT3, -900.0, 86.5)],
     "clock", 6),
    ("race_end_restart", "race_end_lead를 끝까지 돈 뒤 같은 세션에서 재시작(ET 되감기, 차는 그대로) — "
     "두 번째 레이스도 '마지막 랩'·체커",
     443.0, INT_MAX, lead_cars, "restart", 1),
]

RESTART_GAP_S = 30.0        # 재시작 전 공백 (스냅샷 없음)


def build(name, note, E, M, mk, kind, exact_from):
    if kind == "restart":
        return build_restart(name, note, E, M, mk, exact_from)
    cars = mk()
    for c in cars:
        c.build(1200.0)
    player = next(c for c in cars if c.is_player)
    flag, flag_car, fin, leader_cross, _ = truth(cars, E, M)
    pf_t, pf_k = fin[player.id]
    flag_tick = tick_of(flag)
    pf_tick = tick_of(pf_t)
    pcross = [(t, k) for (t, k) in player.cross if k >= 1 and t <= pf_t]

    # 틱 양자화로 정답이 흔들리지 않게 여유를 강제한다
    if E > 0:
        for t in leader_cross + [t for t, _ in pcross]:
            assert abs(t - E) >= 5.0, f"{name}: 시계 0과 라인 통과가 너무 가깝다 ({t:.2f})"
    if flag_car != player.id:
        for t, _ in pcross:
            assert abs(t - flag) >= 5.0, f"{name}: 체커와 내 통과가 너무 가깝다 ({t:.2f})"
    if kind == "net":
        nxt = min(t for t, _ in pcross if t >= flag)
        assert nxt - flag_tick >= 12.0, f"{name}: 안전망 시점에 라인이 너무 가깝다"
    if kind == "clock":
        assert flag_car == player.id, f"{name}: 내가 리더여야 한다"
        nxt = min(t for t, _ in pcross if t >= E)
        assert nxt - tick_of(E) >= 12.0, f"{name}: 시계 0 시점에 라인이 너무 가깝다"

    snaps = []
    n = 0
    end = pf_tick + 10.0
    while round(n / HZ, 3) <= end + 1e-9:
        snaps.append(snapshot(n, cars, player, E, M, flag, fin))
        n += 1

    def at(t):
        return snaps[int(round(t * HZ))]

    def lap_at(t):
        return next(v for v in at(t)["vehicles"] if v["is_player"])["total_laps"]

    crossings = [{"t": round(tick_of(t), 2), "lap": k, "laps_left": pf_k - k}
                 for (t, k) in pcross]
    by_k = {k: t for (t, k) in pcross}
    if kind == "crossing":
        calls_t = [tick_of(by_k[pf_k - 1])]
    elif kind == "net":
        calls_t = [flag_tick]
    elif kind == "clock":
        calls_t = [tick_of(E)]
    else:
        calls_t = [tick_of(by_k[pf_k - 2]), tick_of(by_k[pf_k - 1])]
    final_calls = [{"t": round(t, 2), "lap": lap_at(t) + 1} for t in calls_t]
    fsnap = at(pf_tick)
    me = next(v for v in fsnap["vehicles"] if v["is_player"])
    cp = 1 + sum(1 for v in fsnap["vehicles"]
                 if v["cls"] == me["cls"] and 0 < v["place"] < me["place"])
    sidecar = {
        "scenario": name,
        "note": note,
        "end_et": E,
        "max_laps": M,
        "leader_flag_t": round(flag_tick, 2),
        "crossings": crossings,
        "trace_exact_from_lap": exact_from,
        "final_calls": final_calls,
        "finish": {"t": round(pf_tick, 2), "lap": pf_k, "class_place": cp},
    }
    return snaps, sidecar


def build_restart(name, note, E, M, mk, exact_from):
    snaps, one = build(name, note, E, M, mk, "crossing", exact_from)
    t0 = round(snaps[-1]["t"] + RESTART_GAP_S, 3)
    again = []
    for s in snaps:
        s2 = json.loads(json.dumps(s))
        s2["t"] = round(s["t"] + t0, 3)      # current_et는 그대로 0부터 — ET 되감기
        again.append(s2)

    def shift(race, dt):
        r = {k: race[k] for k in ("crossings", "trace_exact_from_lap", "final_calls", "finish")}
        r = json.loads(json.dumps(r))
        for c in r["crossings"] + r["final_calls"] + [r["finish"]]:
            c["t"] = round(c["t"] + dt, 2)
        r["t0"] = round(dt, 2)
        r["leader_flag_t"] = round(race["leader_flag_t"] + dt, 2)
        return r

    sidecar = {"scenario": name, "note": note, "end_et": E, "max_laps": M,
               "races": [shift(one, 0.0), shift(one, t0)]}
    return snaps + again, sidecar


def write(out_dir, name, snaps, sidecar):
    body = "".join(json.dumps(s, ensure_ascii=False, separators=(",", ":")) + "\n"
                   for s in snaps).encode("utf-8")
    gz = os.path.join(out_dir, name + ".jsonl.gz")
    with open(gz, "wb") as raw:
        # mtime=0·빈 파일명 — 같은 입력이면 같은 바이트 (zlib 버전 차이는 --check가 해제 후 비교)
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
            f.write(body)
    side = os.path.join(out_dir, name + ".truth.json")
    with open(side, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(sidecar, indent=1, sort_keys=True, ensure_ascii=False) + "\n")
    return gz, side


def generate(out_dir, only=None):
    out = []
    for sc in SCENARIOS:
        if only and sc[0] != only:
            continue
        snaps, sidecar = build(*sc)
        out.append((sc[0], len(snaps)) + write(out_dir, sc[0], snaps, sidecar))
    return out


def read_gz(path):
    with gzip.open(path, "rb") as f:
        return f.read()


def main() -> int:
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=os.path.join(root, "data", "replays"))
    ap.add_argument("--only", default=None)
    ap.add_argument("--check", action="store_true",
                    help="임시 폴더에 다시 만들어 저장소 파일과 대조 (압축 해제 바이트)")
    args = ap.parse_args()
    if args.only and args.only not in [s[0] for s in SCENARIOS]:
        print(f"알 수 없는 시나리오: {args.only}")
        return 2

    if not args.check:
        os.makedirs(args.out_dir, exist_ok=True)
        for name, n, gz, side in generate(args.out_dir, args.only):
            print(f"{gz}: 스냅샷 {n}개 (+ {os.path.basename(side)})")
        return 0

    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, n, gz, side in generate(tmp, args.only):
            mine_gz = os.path.join(args.out_dir, name + ".jsonl.gz")
            mine_side = os.path.join(args.out_dir, name + ".truth.json")
            if not (os.path.exists(mine_gz) and os.path.exists(mine_side)):
                print(f"❌ {name}: 파일 없음 — 재생성 필요")
                bad = 1
                continue
            same = read_gz(mine_gz) == read_gz(gz)
            with open(mine_side, "rb") as a, open(side, "rb") as b:
                same = same and a.read() == b.read()
            if same:
                print(f"✅ {name}: 생성기 출력과 동일 (스냅샷 {n}개)")
            else:
                print(f"❌ {name}: 생성기 출력과 다름 — 재생성 필요 "
                      f"(python3 tools/make_race_end_replays.py)")
                bad = 1
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
