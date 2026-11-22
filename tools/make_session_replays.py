"""
세션 브리핑 회귀용 합성 리플레이 생성기 — 연습·퀄리·웜업 (시작/중간 합류) 리플레이 + 기대 콜.

녹화·합성 리플레이는 전부 레이스라 연습·퀄리·웜업 브리핑이 한 번도 재생되지 않았다.
세션마다 10초짜리 리플레이(플레이어 + 멀리 있는 차 한 대)를 만들고, 첫 틱에 나가야 할 브리핑
문장 전체를 data/replays/<이름>.expect.json에 쓴다 (tools/expect_calls.py가 파이썬·C# 각각
'message'까지 대조). 기대 문장은 스냅샷이 아니라 시나리오 값으로 만든 BriefingInputs를
briefing.briefing_text에 넣어 계산한다 — 하니스가 스냅샷에서 재료(세션 종류·중간 합류·노면·
연료·시계)를 잘못 뽑으면 어긋난다. 문장 그룹 순서(세션 종류별 구성)도 여기서 확인한다.

사용법: python tools/make_session_replays.py [--out-dir data/replays] [--check]
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

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from briefing import BriefingInputs, briefing_parts, briefing_text   # noqa: E402
from messages import set_language                                     # noqa: E402

TL = 4200.0
HZ = 5
SECONDS = 10.0
LAP_S = 100.0
GT3 = "LMGT3"
SPOTTER_SEC = 45.0          # config DEFAULTS thresholds.start_spotter_sec (하니스 기본값)

# 이름, 세션 종류, end_et, 첫 틱 세션 시계, 내 완료 랩, 노면, 비, 연료, 기대 문장 그룹
SCENARIOS = [
    ("session_practice", 1, 3600.0, 12.4, 0, 27.0, 0.0, 60.0,
     ["brief_open_practice", "brief_len_long", "brief_temp", "brief_plan_practice",
      "brief_close_out"]),
    ("session_practice_mid", 2, 5400.0, 600.0, 7, 33.6, 0.0, 41.5,
     ["brief_open_practice_mid", "brief_len_remaining", "brief_temp", "brief_close_mid"]),
    ("session_quali", 5, 1200.0, 3.2, 0, 29.4, 0.0, 22.0,
     ["brief_open_quali", "brief_len_long", "brief_temp", "brief_gap_quali",
      "brief_limits_quali", "brief_close_quali"]),
    ("session_quali_mid", 6, 1800.0, 840.6, 3, 18.2, 0.3, 30.0,
     ["brief_open_quali_mid", "brief_len_remaining", "brief_temp", "brief_rain",
      "brief_limits_quali", "brief_close_mid"]),
    ("session_warmup", 9, 900.0, 5.0, 0, 24.0, 0.0, 85.0,
     ["brief_open_warmup", "brief_len_long", "brief_temp", "brief_plan_warmup",
      "brief_close_out"]),
]


def snapshot(n, stype, end_et, et0, laps, temp, rain, fuel):
    t = round(n / HZ, 3)
    et = round(et0 + t, 3)
    vehicles = []
    for cid, drv, off in ((0, "나", 0.0), (1, "리바이", TL / 2)):
        d = (off + t * TL / LAP_S) % TL
        vehicles.append({
            "id": cid, "driver": drv, "vehicle": "Porsche 911 GT3 R", "cls": GT3,
            "is_player": cid == 0, "place": cid + 1,
            "total_laps": laps, "lap_dist": round(d, 1), "path_lat": 0.0 if cid == 0 else 1.5,
            "sector": 1 + int(d / TL * 3) % 3,
            "last_lap": LAP_S if laps else 0.0, "best_lap": LAP_S if laps else 0.0,
            "last_s1": 0.0, "last_s2": 0.0,
            "time_behind_next": 0.0, "laps_behind_next": 0, "time_behind_leader": 0.0,
            "in_pits": False, "pit_state": 0, "num_pitstops": 0, "num_penalties": 0,
            "finish_status": 0, "flag_blue": False, "estimated_lap": LAP_S,
            "time_into_lap": round(et % LAP_S, 3), "lap_start_et": 0.0,
            "pos": [round(math.cos(d / TL * 2 * math.pi) * 900, 1), 0.0,
                    round(math.sin(d / TL * 2 * math.pi) * 900, 1)],
            "in_garage": False,
        })

    def wheel():
        return {"brake_temp": 300.0, "pressure": 170.0, "temps": [80.0, 82.0, 84.0],
                "carcass_temp": 80.0, "wear": 1.0, "flat": False, "detached": False}

    return {
        "t": t, "connected": True, "in_session": True,
        "session": {
            "track": "Circuit de Test", "session_type": stype, "current_et": et,
            "end_et": end_et, "max_laps": 2147483647, "track_len": TL, "game_phase": 5,
            "yellow_state": 0, "sector_flags": [0, 0, 0], "pit_speed_limit": 60.0,
            "in_realtime": True, "raining": rain, "dark_cloud": 0.1,
            "ambient_temp": 20.0, "track_temp": temp, "avg_wetness": rain,
            "num_vehicles": 2,
        },
        "player": {
            "id": 0, "lap_number": laps + 1, "lap_start_et": 0.0, "speed_kmh": 151.2,
            "rpm": 7000, "max_rpm": 9250, "gear": 4, "fuel": fuel, "fuel_capacity": 90.0,
            "water_temp": 85.0, "oil_temp": 98.0, "overheating": False, "detached": False,
            "dent_severity": [0] * 8, "last_impact_et": 0.0, "last_impact_mag": 0.0,
            "in_pitlane": False, "speed_limiter": False,
            "wheels": [wheel(), wheel(), wheel(), wheel()],
        },
        "vehicles": vehicles,
    }


def build(name, stype, end_et, et0, laps, temp, rain, fuel, groups):
    snaps = [snapshot(n, stype, end_et, et0, laps, temp, rain, fuel)
             for n in range(int(SECONDS * HZ) + 1)]
    # 기대 브리핑 — 시나리오 값으로 직접 (연습/퀄리/웜업의 중간 합류는 시계 120초 초과 또는 완료 랩)
    b = BriefingInputs(session_type=stype, end_et=end_et, cur_et=et0, max_laps=2147483647,
                       mid_join=et0 > 120 or laps > 0, track_temp=temp, raining=rain,
                       fuel=fuel, start_spotter_sec=SPOTTER_SEC)
    got = [k for k, _ in briefing_parts(b)]
    assert got == groups, f"{name}: 브리핑 구성 {got} ≠ {groups}"
    expect = {
        "scenario": name,
        "note": "세션 시작 브리핑 — 무전 체크로 열고 세션 종류별 구성, 트랙 이름 없음",
        "types": ["session_briefing"],
        "events": [{"t": 0.0, "type": "session_briefing", "data": {},
                    "message": briefing_text(b)}],
    }
    return snaps, expect


def write(out_dir, name, snaps, expect):
    body = "".join(json.dumps(s, ensure_ascii=False, separators=(",", ":")) + "\n"
                   for s in snaps).encode("utf-8")
    gz = os.path.join(out_dir, name + ".jsonl.gz")
    with open(gz, "wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
            f.write(body)
    side = os.path.join(out_dir, name + ".expect.json")
    with open(side, "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(expect, indent=1, sort_keys=True, ensure_ascii=False) + "\n")
    return gz, side


def generate(out_dir):
    set_language("en")
    out = []
    for sc in SCENARIOS:
        snaps, expect = build(*sc)
        out.append((sc[0], len(snaps)) + write(out_dir, sc[0], snaps, expect))
    return out


def read_gz(path):
    with gzip.open(path, "rb") as f:
        return f.read()


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", default=os.path.join(ROOT, "data", "replays"))
    ap.add_argument("--check", action="store_true",
                    help="임시 폴더에 다시 만들어 저장소 파일과 대조 (압축 해제 바이트)")
    args = ap.parse_args()
    if not args.check:
        os.makedirs(args.out_dir, exist_ok=True)
        for name, n, gz, side in generate(args.out_dir):
            print(f"{gz}: 스냅샷 {n}개 (+ {os.path.basename(side)})")
        return 0
    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, n, gz, side in generate(tmp):
            mine_gz = os.path.join(args.out_dir, name + ".jsonl.gz")
            mine_side = os.path.join(args.out_dir, name + ".expect.json")
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
                      f"(python3 tools/make_session_replays.py)")
                bad = 1
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
