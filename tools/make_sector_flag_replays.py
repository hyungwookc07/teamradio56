"""
섹터 로컬 옐로 회귀용 합성 리플레이 생성기 — 리플레이 + 기대 콜(expect) 사이드카.

버그: 섹터 옐로 콜이 '0 이하 → 1..2' 전이만 인정하고 2 넘는 값은 쓰레기로 봤다. LMU는 깨끗한
섹터를 11, 로컬 옐로를 1로 쓴다(lmu-pitwall 인게임 측정 LMU 1.4, LmuOverlay 검증 목록 — CrewChief·
TinyPedal·SimHub·pitwall 모두 '== 1'만 옐로). 그래서 실제 옐로 11 → 1이 한 번도 콜되지 않았다.
녹화·합성 리플레이는 전부 rF2식 0 기준이라 이 경로가 재생된 적이 없다.

규칙 (기대값의 근거, analyzers/racecontrol.py _check_sector_yellow · C# CheckSectorYellow와 같은 값):
  · 옐로 = 원시값 1. 0·11·2·7·255·-1 등 나머지는 전부 옐로 아님. 배열 i = 섹터 i+1
  · 콜 = 직전 샘플 옐로 아님 → 옐로 (엣지). 첫 샘플은 기록만
  · FCY 중(페이즈 6 또는 mYellowFlagState 1..7)은 콜하지 않는다 — 직전 값은 그래도 매 틱 갱신
  · 같은 섹터 재콜은 120초 뒤 (분석기가 푸시 전에 기록 — 버스가 거절해도)
  · 버스: sector_yellow 종류 쿨다운 45초(마지막 수락 기준), dedup 키 syellow_<i>. 리플레이 하니스는
    버스를 꺼내지 않아 키마다 리플레이당 한 번만 수락되고, 세션 전환(ET 30초 넘게 되감기)에서만
    대기 키를 비운다 (분석기는 리셋하지 않고, 종류 쿨다운 시각도 남는다)

리플레이 (전부 5Hz, 플레이어 + 반 바퀴 떨어진 차 한 대 — 트래픽·랩 완료 없음):
  · sector_yellow_lmu — 11 기준, S1 11 → 1 → 콜 하나. 1로 남은 채 ET 되감기(대기 키 비움):
    레벨 트리거(1이면 120초마다)라면 첫 콜 120초 뒤에 다시 나간다
  · sector_yellow_join — 첫 샘플 [11,1,1]: 이미 켜진 S2·S3는 침묵. S2가 풀렸다 다시 1 → 콜,
    S3는 끝까지 1 → 침묵
  · sector_yellow_rf2 — rF2식 0 기준: 0 → 1은 그대로 콜 (S2, 60초 뒤 S1)
  · sector_yellow_junk — S1이 2·7·255·0·2 사이를 오가도 침묵. 255 → 1(S2) · -1 → 1(S3) ·
    2 → 1(S1)은 콜. -1은 일부러 원시 JSON에 쓴다: 파이썬은 -1, C# 로더는 바이트 캐스트(255)로
    읽는다 — '== 1' 규칙은 어느 쪽이든 옐로 아님이라 같고, 예전 '0 이하' 규칙은 여기서 두 엔진이
    갈렸다 (파이썬만 콜)
  · sector_yellow_fcy — 레이스: 로컬 옐로(S2) 콜 → FCY 선언과 함께 전 섹터 1 → 피트 오픈 →
    그린(리스타트 준비 6이 남은 채) → 그 사이 S3가 풀렸다 다시 1 → 옐로 상태 0 → 섹터 정리 →
    S1 새 옐로. FCY·리스타트 준비 동안 섹터 콜 없음, FCY가 끝난 순간 1로 남은 섹터도 침묵(억제 중
    직전 값을 안 갱신하면 여기서 S1이 나간다), 끝난 뒤 새 옐로는 콜. FCY·피트 오픈·그린 콜도 대조
  · sector_yellow_cooldown — 같은 섹터 120초: S1 콜 → 40초 뒤 S3 → 1은 종류 쿨다운에 거절(기록은
    남음) → 그 65초·115초 뒤 다시 1은 침묵, 122초 뒤는 콜
  · sector_yellow_double — 같은 틱 S1·S2 → 1: 낮은 번호(S1) 하나만 (S2는 종류 쿨다운에 거절되고
    기록돼, 1로 남은 동안 쿨다운이 풀려도 다시 안 부른다). 60초 뒤 S3 → 1은 콜
생성기는 자기 규칙 사본으로 기대 콜을 계산해 시나리오에 적은 정답과 맞춘다. 예전 규칙('0 이하 →
1..2', FCY 억제 없음)이라면 rf2 말고는 결과가 달라진다는 것(이 리플레이가 회귀를 잡는다는 근거),
새 규칙은 원시값을 부호 있게 읽든 바이트로 읽든 결과가 같다는 것도 확인한다.

사용법: python tools/make_sector_flag_replays.py [--out-dir data/replays] [--check]
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

from messages import msg, set_language      # noqa: E402

TL = 4200.0
HZ = 5
LAP_S = 100.0
GT3 = "LMGT3"
ET0 = 12.4
END_ET = 3600.0
PRACTICE = 1
RACE = 10

# 규칙 사본 (racecontrol.py · RaceControlAnalyzer.cs와 같은 값 — 분석기를 import하지 않는다)
YELLOW = 1
PHASE_FCY = 6
Y_FCY = (1, 7)
REARM_S = 120.0                 # 같은 섹터 재콜 간격
TYPE_COOLDOWN_S = 45.0          # config DEFAULTS cooldowns.sector_yellow (하니스 기본값)
RESTART_ET_BACK = 30.0          # state.update 세션 전환: ET가 이만큼 넘게 뒤로 가면

C = [11, 11, 11]                # LMU 깨끗함

# 이름, 세션 종류, 길이(초), 단계 [(t, 바뀌는 값)], 기대 섹터 콜 [(t, 배열 인덱스)],
# 다른 기대 콜 [(t, 종류, data)], 설명
# 단계 값: flags(섹터 3개), phase, yellow, rewind(ET를 이만큼 뒤로 — 세션 재시작)
SCENARIOS = [
    dict(name="sector_yellow_lmu", stype=PRACTICE, seconds=180.0,
         steps=[(0.0, {"flags": C}),
                (20.0, {"flags": [1, 11, 11]}),
                (60.0, {"rewind": 60.0})],
         want=[(20.0, 0)],
         note="LMU 11 기준 S1 11 → 1 콜 하나, 1로 남은 동안 반복 없음 (60초 ET 되감기로 대기 키를 "
              "비워도 — 레벨 트리거라면 140초에 다시 나간다)"),
    dict(name="sector_yellow_join", stype=PRACTICE, seconds=100.0,
         steps=[(0.0, {"flags": [11, 1, 1]}),
                (30.0, {"flags": [11, 11, 1]}),
                (60.0, {"flags": [11, 1, 1]})],
         want=[(60.0, 1)],
         note="첫 샘플에 이미 켜진 S2·S3는 침묵, S2가 풀렸다 다시 1이면 콜, 끝까지 1인 S3는 침묵"),
    dict(name="sector_yellow_rf2", stype=PRACTICE, seconds=120.0,
         steps=[(0.0, {"flags": [0, 0, 0]}),
                (20.0, {"flags": [0, 1, 0]}),
                (40.0, {"flags": [0, 0, 0]}),
                (80.0, {"flags": [1, 0, 0]}),
                (100.0, {"flags": [0, 0, 0]})],
         want=[(20.0, 1), (80.0, 0)],
         note="rF2식 0 기준 0 → 1은 그대로 콜 (예전 규칙과 같은 결과 — 회귀 방지)"),
    dict(name="sector_yellow_junk", stype=PRACTICE, seconds=230.0,
         steps=[(0.0, {"flags": C}),
                (10.0, {"flags": [2, 11, 11]}),
                (20.0, {"flags": [7, 11, 11]}),
                (30.0, {"flags": [255, 11, 11]}),
                (40.0, {"flags": [0, 11, 11]}),
                (50.0, {"flags": [2, 11, 11]}),
                (60.0, {"flags": C}),
                (70.0, {"flags": [11, 255, 11]}),
                (80.0, {"flags": [11, 1, 11]}),
                (90.0, {"flags": C}),
                (130.0, {"flags": [11, 11, -1]}),
                (140.0, {"flags": [11, 11, 1]}),
                (150.0, {"flags": C}),
                (190.0, {"flags": [2, 11, 11]}),
                (200.0, {"flags": [1, 11, 11]}),
                (210.0, {"flags": C})],
         want=[(80.0, 1), (140.0, 2), (200.0, 0)],
         note="1 아닌 값(2·7·255·0·-1) 사이 전이는 침묵, 255 → 1 · -1 → 1 · 2 → 1은 콜 "
              "(-1은 원시 JSON — 파이썬 -1, C# 바이트 255; 예전 규칙은 -1 → 1에서 엔진이 갈렸다)"),
    dict(name="sector_yellow_fcy", stype=RACE, seconds=300.0,
         steps=[(0.0, {"flags": C, "phase": 5, "yellow": 0}),
                (30.0, {"flags": [11, 1, 11]}),
                (90.0, {"flags": [1, 1, 1], "phase": 6, "yellow": 1}),
                (95.0, {"yellow": 2}),
                (130.0, {"yellow": 4}),
                (170.0, {"yellow": 5}),
                (190.0, {"phase": 5, "yellow": 6}),
                (195.0, {"flags": [1, 1, 11]}),
                (198.0, {"flags": [1, 1, 1]}),
                (205.0, {"yellow": 0}),
                (230.0, {"flags": C}),
                (270.0, {"flags": [1, 11, 11]})],
         want=[(30.0, 1), (270.0, 0)],
         other=[(90.0, "fcy", {"pool": "fcy_start"}),
                (130.0, "fcy_pit_open", {"pool": "fcy_pit_open"}),
                (190.0, "green_flag", {"pool": "green_flag"})],
         note="FCY(페이즈 6)·리스타트 준비(그린 + 옐로 상태 6) 동안 전 섹터 1이어도 섹터 콜 없음, "
              "FCY가 끝난 순간 1로 남은 섹터도 침묵, 끝난 뒤 새 옐로(S1)는 콜"),
    dict(name="sector_yellow_cooldown", stype=PRACTICE, seconds=200.0,
         steps=[(0.0, {"flags": C}),
                (20.0, {"flags": [1, 11, 11]}),
                (30.0, {"flags": C}),
                (60.0, {"flags": [11, 11, 1]}),
                (70.0, {"flags": C}),
                (125.0, {"flags": [11, 11, 1]}),
                (135.0, {"flags": C}),
                (175.0, {"flags": [11, 11, 1]}),
                (178.0, {"flags": C}),
                (182.0, {"flags": [11, 11, 1]}),
                (190.0, {"flags": C})],
         want=[(20.0, 0), (182.0, 2)],
         note="같은 섹터 120초 — S3 60초 엣지는 종류 쿨다운에 거절돼도 기록, 125·175초(65·115초 뒤) "
              "침묵, 182초(122초 뒤) 콜"),
    dict(name="sector_yellow_double", stype=PRACTICE, seconds=140.0,
         steps=[(0.0, {"flags": C}),
                (20.0, {"flags": [1, 1, 11]}),
                (80.0, {"flags": [1, 1, 1]})],
         want=[(20.0, 0), (80.0, 2)],
         note="같은 틱 S1·S2 → 1이면 낮은 번호 하나만 (S2는 종류 쿨다운에 거절·기록, 1로 남은 동안 "
              "다시 안 부름), 60초 뒤 S3 → 1은 콜"),
]


def at(steps, t):
    """t 시점의 값 (단계는 그 시각부터 적용) + 그때까지 되감은 ET 합."""
    cur = {"flags": list(C), "phase": 5, "yellow": 0}
    back = 0.0
    for ts, ch in steps:
        if ts > t + 1e-9:
            break
        for k, v in ch.items():
            if k == "rewind":
                back += v
            else:
                cur[k] = list(v) if k == "flags" else v
    return cur, back


def snapshot(n, sc):
    t = round(n / HZ, 3)
    cur, back = at(sc["steps"], t)
    et = round(ET0 + t - back, 3)
    vehicles = []
    for cid, drv, off in ((0, "나", 0.0), (1, "리바이", TL / 2)):
        d = (off + t * TL / LAP_S) % TL
        vehicles.append({
            "id": cid, "driver": drv, "vehicle": "Porsche 911 GT3 R", "cls": GT3,
            "is_player": cid == 0, "place": cid + 1,
            "total_laps": 0, "lap_dist": round(d, 1), "path_lat": 0.0 if cid == 0 else 1.5,
            "sector": 1 + int(d / TL * 3) % 3,
            "last_lap": 0.0, "best_lap": 0.0, "last_s1": 0.0, "last_s2": 0.0,
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
            "track": "Circuit de Test", "session_type": sc["stype"], "current_et": et,
            "end_et": END_ET, "max_laps": 2147483647, "track_len": TL,
            "game_phase": cur["phase"], "yellow_state": cur["yellow"],
            "sector_flags": cur["flags"], "pit_speed_limit": 60.0,
            "in_realtime": True, "raining": 0.0, "dark_cloud": 0.1,
            "ambient_temp": 20.0, "track_temp": 27.0, "avg_wetness": 0.0,
            "num_vehicles": 2,
        },
        "player": {
            "id": 0, "lap_number": 1, "lap_start_et": 0.0, "speed_kmh": 151.2,
            "rpm": 7000, "max_rpm": 9250, "gear": 4, "fuel": 60.0, "fuel_capacity": 90.0,
            "water_temp": 85.0, "oil_temp": 98.0, "overheating": False, "detached": False,
            "dent_severity": [0] * 8, "last_impact_et": 0.0, "last_impact_mag": 0.0,
            "in_pitlane": False, "speed_limiter": False,
            "wheels": [wheel(), wheel(), wheel(), wheel()],
        },
        "vehicles": vehicles,
    }


def fcy(phase, yellow):
    return phase == PHASE_FCY or Y_FCY[0] <= yellow <= Y_FCY[1]


def model(snaps, rule="new", as_byte=False):
    """
    수락될 섹터 옐로 콜 [(t, i)] — 분석기 규칙 + 버스(종류 쿨다운·대기 키) + 하니스(세션 전환에서
    대기 키 비움). rule="old"는 예전 '0 이하 → 1..2'(FCY 억제 없음, 모자란 직전 값은 0).
    as_byte=True면 원시값을 C# 로더처럼 바이트로 읽는다 (-1 → 255).
    """
    prev, stamp, pending, last_ok, last_et, out = None, {}, set(), None, None, []
    for s in snaps:
        ses, t = s["session"], s["t"]
        flags = [(v & 0xFF) if as_byte else v for v in ses["sector_flags"][:3]]
        old_prev, prev = prev, flags
        gated = rule == "new" and fcy(ses["game_phase"], ses["yellow_state"])
        if old_prev is not None and not gated:
            for i, f in enumerate(flags):
                if rule == "new":
                    w = old_prev[i] if i < len(old_prev) else None
                    edge = w != YELLOW and f == YELLOW
                else:
                    w = old_prev[i] if i < len(old_prev) else 0
                    edge = w <= 0 < f <= 2
                if not edge or (i in stamp and t - stamp[i] < REARM_S):
                    continue
                stamp[i] = t                        # 버스가 거절해도 기록
                if i in pending or (last_ok is not None and t - last_ok < TYPE_COOLDOWN_S):
                    continue
                last_ok = t
                pending.add(i)
                out.append((t, i))
        # 하니스: 분석기 틱 뒤 state.update가 세션 전환을 보면 대기 키를 비운다
        if last_et is not None and ses["current_et"] < last_et - RESTART_ET_BACK:
            pending.clear()
        last_et = ses["current_et"]
    return out


def build(sc):
    snaps = [snapshot(n, sc) for n in range(int(sc["seconds"] * HZ) + 1)]
    name = sc["name"]
    got = model(snaps)
    want = [(float(t), i) for t, i in sc["want"]]
    assert got == want, f"{name}: 규칙 사본 {got} ≠ 시나리오 정답 {want}"
    # 새 규칙은 원시값을 부호 있게 읽든 바이트로 읽든 같다 (파이썬 int ↔ C# byte)
    assert model(snaps, as_byte=True) == got, name
    old_py = model(snaps, rule="old")
    old_cs = model(snaps, rule="old", as_byte=True)
    if name == "sector_yellow_rf2":
        assert old_py == old_cs == got, (name, old_py, old_cs)    # rF2식 데이터는 그대로
    else:
        # 예전 규칙이면 결과가 다르다 — 이 리플레이가 회귀를 잡는다는 근거
        assert old_py != got and old_cs != got, (name, old_py, old_cs)
    if name == "sector_yellow_junk":
        assert old_py != old_cs, (name, old_py)    # 예전 규칙은 -1 → 1에서 두 엔진이 갈렸다
    events = [{"t": t, "type": "sector_yellow", "data": {},
               "message": msg("sector_yellow", n=i + 1)} for t, i in got]
    events += [{"t": float(t), "type": typ, "data": data} for t, typ, data in sc.get("other", [])]
    events.sort(key=lambda e: e["t"])
    types = ["sector_yellow"] + sorted({typ for _, typ, _ in sc.get("other", [])})

    def calls(lst):
        return [{"t": t, "sector": i + 1} for t, i in lst]

    expect = {
        "scenario": name,
        "note": sc["note"],
        "types": types,
        "events": events,
        "old_rule_calls_py": calls(old_py),
        "old_rule_calls_cs": calls(old_cs),
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
        snaps, expect = build(sc)
        out.append((sc["name"], len(snaps)) + write(out_dir, sc["name"], snaps, expect))
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
                      f"(python3 tools/make_sector_flag_replays.py)")
                bad = 1
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
