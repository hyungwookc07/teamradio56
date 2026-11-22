"""
트래픽 클래스 판정 회귀용 합성 리플레이 생성기 — 리플레이 + 기대 콜(expect) 사이드카.

사용자 제보: "하이퍼카나 LMP를 타고 있어도 뒤에서 오는 차가 다 '패스터 카'라고 콜이 옴".
예전 판정은 서열표(부분 문자열 "hyper"/"lmp2"/…)와 호칭표("hypercar"/…)가 따로 놀았다:
  · "Hyper"처럼 서열표에만 맞는 문자열 → 하이퍼카 뒤 하이퍼카가 "faster car right behind"
  · 어느 표에도 없는 문자열(LMP3, 낯선 클래스) → 서열 0 → 다른 클래스는 전부 '빠른 차'
    (접근 예고 "Faster traffic, faster car in …"), 내가 LMP3면 GT3도 빠른 차, 백마커는 안 잡힘
  · "LMP2" ↔ "LMP2_ELMS"는 문자열이 달라 같은 클래스의 랩핑(블루 플래그) 접근 예고가 없었다
녹화 리플레이(race_full, traffic2)는 Hypercar/LMGT3 두 문자열뿐이라 이 경로가 재생된 적이 없다.

리플레이 네 개 (data/replays/traffic_class_<이름>.jsonl.gz + .expect.json — tools/expect_calls.py가
파이썬·C# 둘 다 트래픽 콜만 대조, 풀·슬롯 "voice"까지 — 클래스 호칭이 "faster car"가 아님을 확인):
  · hyper   — 나 "Hyper": 같은 "Hyper"·LMP2·LMP3·LMGT3가 뒤에서 붙어 지나감 (전부 빠른 차 아님 →
              접근 예고 없음, 근접 콜 호칭 Hypercar/LMP2/LMP3/GT3), 앞 LMGT3를 잡음 (백마커)
  · lmp3    — 나 "LMP3": "Hyper"가 빠르게 옴 (접근 예고, 호칭 Hypercar), LMGT3가 뒤에서 붙음
              (빠른 차 아님), 앞 LMGT3를 잡음 (백마커)
  · lmp2    — 나 "LMP2": 같은 계열 "LMP2_ELMS"가 뒤에서 붙음 (빠른 차 아님, 호칭 LMP2), 한 랩 앞선
              "LMP2_ELMS"가 랩 돌리러 옴 (랩핑 → 접근 예고), 앞 "LMP2 Pro-Am"을 잡음 (백마커 아님)
  · unknown — 나 "LMGT3": 계열 모르는 "Prototype"이 베스트 랩 중앙값 6.7% 빠름 → 접근 예고
              (클래스 이름 없는 traffic_approach_any), 페이스 없는 "Mystery"는 접근 예고 없이 근접
              콜만 (호칭 없음), 8.3% 느린 "Slow Cup"을 잡음 (페이스로 백마커)

규칙 (기대값의 근거 — analyzers/traffic.py · vehicle_class.py, C# 같은 값):
  · 같은 클래스(문자열이 같거나 같은 계열)는 한 랩 가까이(0.9랩) 앞서 랩 돌리러 올 때만 빠른 차.
    다른 계열은 서열 Hypercar > LMP2 > LMP3 > GTE > GT3, 계열을 모르면 클래스 문자열별 베스트 랩
    중앙값이 2% 이상 빠르면 빠른 차 / 느리면 백마커, 페이스가 없으면 둘 다 아님
  · 접근 예고 = 빠른 차가 50m 밖 뒤에서 초속 2m 이상 좁혀 도달 10초 안 (gap_sec = 반올림, 최대 6)
  · 근접(nearby_behind) = 뒤 50m 안으로 FAR/접근 예고에서 들어올 때, 나란히 = |갭| ≤ 4.6m +
    min(|접근 속도| × 0.5초, 5m) (오른쪽 2.5m → alongside_right), 지나감(pass_complete) = 나란히에서
    앞 50m 안으로, 백마커 = 앞 50m 안으로 FAR에서 (백마커일 때만), 떨어짐(dropped) = 근접·나란히였던
    빠르지 않은 차가 뒤 50m 밖으로
  · 쿨다운 — 접근 예고 20초, 근접/나란히 8초(긴급은 절반), 지나감/백마커/떨어짐 15초 — 기대 콜끼리
    그 밖으로 띄운다 (생성 시점에 확인). 차 하나씩만 가까이 와서 다중 종합은 없다

모델: 내 차는 초속 50m 등속(트랙 6000m, 랩 120초), 세션 중간(랩 3)부터 그린. 상대는 내 차 기준
갭으로 움직인다 — 대기 갭에서 같이 달리다가(FAR), 정해진 시각부터 상대 속도로 다가와 지나가고,
반대편 대기 갭에서 다시 같이 달린다. 위치는 시각에서 해석적으로 구한다 (dt 누적 없음).
생성기는 예전 규칙(a48cf98)으로는 기대가 깨지는 경우도 적어 둔다 (cases[].old).

사용법: python tools/make_traffic_class_replay.py [--out-dir data/replays] [--check]
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

PREFIX = "traffic_class"
TL = 6000.0                 # 트랙 길이 (m)
HZ = 5
V = 50.0                    # 내 속도 (m/s) — 랩 120초
LAP_S = TL / V
ET0 = 260.0                 # 리플레이 첫 틱의 세션 시각 (랩 3 중간)
END_ET = 3600.0             # 시간제 1시간
INT_MAX = 2147483647
PLAYER = 0
PARK = 150.0                # 대기 갭 (m) — 50m 근접 밖, 접근 속도 0이라 FAR
SLOT_S = 70.0               # 차 하나에 쓰는 시간 (콜 쿨다운이 서로 겹치지 않게)
LEAD_S = 10.0               # 첫 차가 움직이기 시작하는 시각

# 분석기 임계 (analyzers/traffic.py, config DEFAULTS)
PROX = 50.0
ALONG = 4.6
LEAD_MAX = 5.0
CALL_LAT = 0.5
ETA_WARN = 10.0
MARGIN_M = 0.05             # 판정 경계와 틱 값 사이 최소 여유 (반올림·EMA 오차가 결과를 못 바꾸게)
COOLDOWN = {"traffic_approach": (20.0, True), "traffic_close": (8.0, True),
            "traffic_update": (15.0, False)}     # (초, 긴급이면 절반 지나면 통과)

# 경우: 차 하나가 하는 일. kind = pass(뒤에서 와서 지나감) | catch(앞 차를 내가 잡음).
#   faster / backmarker = 이 규칙에서의 정답, show = 무전 호칭 ("" = 클래스 이름 없이),
#   lap = 한 랩 앞선 차, best = 그 차의 베스트 랩 (0 = 아직 없음), old = 예전 규칙에서 깨지는 점
SCENARIOS = {
    "hyper": dict(
        me="Hyper", my_best=207.0,
        cars=[
            dict(id=1, driver="하이퍼 동료", vehicle="Porsche 963", cls="Hyper", best=207.4,
                 kind="pass", vr=8.0, faster=False, show="Hypercar",
                 note="같은 \"Hyper\" 하이퍼카가 뒤에서 붙음 — 빠른 차 아님, 호칭 Hypercar",
                 old="근접·나란히·지나감 호칭이 faster car"),
            dict(id=2, driver="LMP2 드라이버", vehicle="Oreca 07", cls="LMP2", best=214.0,
                 kind="pass", vr=8.0, faster=False, show="LMP2",
                 note="LMP2가 뒤에서 붙음 (내 페이스가 떨어진 구간) — 빠른 차 아님",
                 old=None),
            dict(id=3, driver="LMP3 드라이버", vehicle="Ligier JS P320", cls="LMP3", best=223.0,
                 kind="pass", vr=8.0, faster=False, show="LMP3",
                 note="LMP3가 뒤에서 붙음 — 빠른 차 아님, 호칭 LMP3",
                 old="서열 모름 → 빠른 차: 접근 예고 + 호칭 faster car"),
            dict(id=4, driver="GT3 드라이버", vehicle="BMW M4 GT3", cls="LMGT3", best=236.0,
                 kind="pass", vr=6.0, faster=False, show="GT3",
                 note="LMGT3가 뒤에서 붙음 — 빠른 차 아님", old=None),
            dict(id=5, driver="GT3 백마커", vehicle="Ferrari 296 GT3", cls="LMGT3", best=237.0,
                 kind="catch", vr=6.0, faster=False, backmarker=True, show="GT3",
                 note="앞 LMGT3를 잡음 — 백마커 예고 (느린 계열)", old=None),
        ]),
    "lmp3": dict(
        me="LMP3", my_best=223.0,
        cars=[
            dict(id=1, driver="하이퍼 리더", vehicle="Ferrari 499P", cls="Hyper", best=207.0,
                 kind="pass", vr=8.0, faster=True, show="Hypercar",
                 note="\"Hyper\"가 빠르게 옴 — 접근 예고, 호칭 Hypercar",
                 old="호칭 faster car"),
            dict(id=2, driver="GT3 드라이버", vehicle="Aston Vantage GT3", cls="LMGT3", best=236.0,
                 kind="pass", vr=6.0, faster=False, show="GT3",
                 note="LMGT3가 뒤에서 붙음 — 빠른 차 아님 (LMP3 > GT3)",
                 old="내 클래스 서열 모름 → 빠른 차: 접근 예고"),
            dict(id=3, driver="GT3 백마커", vehicle="McLaren 720S GT3", cls="LMGT3", best=237.0,
                 kind="catch", vr=6.0, faster=False, backmarker=True, show="GT3",
                 note="앞 LMGT3를 잡음 — 백마커 예고",
                 old="내 클래스 서열 모름 → 백마커 아님: 예고 없음, 빠른 차라 떨어짐 콜도 없음"),
        ]),
    "lmp2": dict(
        me="LMP2", my_best=214.0,
        cars=[
            dict(id=1, driver="ELMS 동료", vehicle="Oreca 07", cls="LMP2_ELMS", best=214.3,
                 kind="pass", vr=8.0, faster=False, show="LMP2",
                 note="같은 계열 \"LMP2_ELMS\"가 같은 랩에서 뒤에 붙음 — 배틀, 빠른 차 아님",
                 old=None),
            dict(id=2, driver="ELMS 리더", vehicle="Oreca 07", cls="LMP2_ELMS", best=213.5,
                 kind="pass", vr=8.0, faster=True, lap=1, show="LMP2",
                 note="한 랩 앞선 \"LMP2_ELMS\"가 랩 돌리러 옴 — 같은 계열 랩핑 → 접근 예고",
                 old="문자열이 달라 같은 클래스 랩핑으로 안 봄 → 접근 예고 없음"),
            dict(id=3, driver="프로암", vehicle="Oreca 07", cls="LMP2 Pro-Am", best=216.0,
                 kind="catch", vr=6.0, faster=False, backmarker=False, show="LMP2",
                 note="앞 \"LMP2 Pro-Am\"을 잡음 — 같은 계열이라 백마커 아님 (나란히·떨어짐만)",
                 old=None),
        ]),
    "unknown": dict(
        me="LMGT3", my_best=120.0,
        cars=[
            dict(id=9, driver="GT3 동료", vehicle="Lexus RC F GT3", cls="LMGT3", best=120.5,
                 kind="park", show="GT3", note="대기만 — 내 클래스 페이스 중앙값 (120.25)", old=None),
            dict(id=8, driver="프로토 동료", vehicle="Prototype B", cls="Prototype", best=112.5,
                 kind="park", show="", note="대기만 — \"Prototype\" 페이스 중앙값 (112.25)", old=None),
            dict(id=1, driver="프로토", vehicle="Prototype A", cls="Prototype", best=112.0,
                 kind="pass", vr=8.0, faster=True, show="",
                 note="계열 모르는 \"Prototype\" — 페이스 6.7% 빠름 → 접근 예고 (클래스 이름 없이)",
                 old="호칭 faster car (traffic_approach)"),
            dict(id=2, driver="미스터리", vehicle="Mystery Car", cls="Mystery", best=0.0,
                 kind="pass", vr=8.0, faster=False, show="",
                 note="계열·페이스 모두 모르는 \"Mystery\" — 접근 예고 없이 근접·나란히 (호칭 없음)",
                 old="서열 모름 → 빠른 차: 접근 예고 + 호칭 faster car"),
            dict(id=3, driver="슬로우", vehicle="Slow Car", cls="Slow Cup", best=130.0,
                 kind="catch", vr=6.0, faster=False, backmarker=True, show="",
                 note="계열 모르는 \"Slow Cup\" — 페이스 8.3% 느림 → 백마커 예고 (호칭 없음)",
                 old="서열 모름 → 빠른 차: 백마커 예고·떨어짐 콜 없음"),
        ]),
}


def tick(t):
    return int(round(t * HZ))


def gap_schedule(car, slot):
    """(시작 시각, 시작 갭, 상대 속도, 끝 갭) — 갭은 + = 내 앞."""
    if car["kind"] == "park":
        return 0.0, -PARK * 2 if car["id"] % 2 else PARK * 2, 0.0, None
    t0 = LEAD_S + slot * SLOT_S
    if car["kind"] == "pass":
        return t0, -PARK, car["vr"], PARK
    return t0, PARK, -car["vr"], -PARK


def gap_at(car, slot, t):
    t0, g0, vr, g1 = gap_schedule(car, slot)
    if t <= t0 or vr == 0.0:
        return g0
    g = g0 + vr * (t - t0)
    return min(g, g1) if vr > 0 else max(g, g1)


def build_snaps(sc, n_ticks):
    cars = sc["cars"]
    snaps = []
    for n in range(n_ticks):
        t = round(n / HZ, 3)
        et = round(ET0 + t, 3)
        dme = V * et                 # 내 누적 거리 — 세션 시작부터 등속
        rows = [(None, dme)]
        for slot, c in enumerate(c for c in cars if c["kind"] != "park"):
            rows.append((c, dme + gap_at(c, slot, t) + (TL if c.get("lap") else 0.0)))
        for c in cars:
            if c["kind"] == "park":
                rows.append((c, dme + gap_at(c, 0, t)))
        ranked = sorted(rows, key=lambda r: (-r[1], r[0]["id"] if r[0] else PLAYER))
        place = {(r[0]["id"] if r[0] else PLAYER): i + 1 for i, r in enumerate(ranked)}
        lead_d = ranked[0][1]
        vehicles = []
        for c, d in rows:
            cid = c["id"] if c else PLAYER
            i = next(k for k, r in enumerate(ranked) if r[0] is c)
            laps = int(math.floor(d / TL))
            ld = d - laps * TL
            if c is None:
                best = last = sc["my_best"]
            else:
                best = last = c["best"]
            lse = round(et - ld / V, 3)
            ang = ld / TL * 2 * math.pi
            r = 950.0 + (0.0 if c is None else 2.5)
            vehicles.append({
                "id": cid, "driver": "나" if c is None else c["driver"],
                "vehicle": "Player Car" if c is None else c["vehicle"],
                "cls": sc["me"] if c is None else c["cls"],
                "is_player": c is None, "place": place[cid],
                "total_laps": laps,
                "lap_dist": round(ld, 3),
                "path_lat": 0.0 if c is None else 2.5,
                "sector": 1 + int(ld / TL * 3) % 3,
                "last_lap": round(last, 3),
                "best_lap": round(best, 3),
                "last_s1": round(last / 3, 3) if last else 0.0,
                "last_s2": round(last * 2 / 3, 3) if last else 0.0,
                "time_behind_next": round(max(ranked[i - 1][1] - d, 0.0) / V, 3) if i > 0 else 0.0,
                "laps_behind_next": max(int(math.floor((ranked[i - 1][1] - d) / TL)), 0) if i > 0 else 0,
                "time_behind_leader": round(max(lead_d - d, 0.0) / V, 3),
                "in_pits": False, "pit_state": 0, "num_pitstops": 0,
                "num_penalties": 0, "finish_status": 0, "flag_blue": False,
                "estimated_lap": LAP_S,
                "time_into_lap": round(et - lse, 3),
                "lap_start_et": lse,
                "pos": [round(math.cos(ang) * r, 3), 0.0, round(math.sin(ang) * r, 3)],
                "in_garage": False,
            })
        pd = dme
        plaps = int(math.floor(pd / TL))
        plse = round(et - (pd - plaps * TL) / V, 3)

        def wheel():
            return {"brake_temp": 350.0, "pressure": 172.0, "temps": [85.0, 88.0, 91.0],
                    "carcass_temp": 85.0, "wear": 0.97, "flat": False, "detached": False}

        snaps.append({
            "t": et, "connected": True, "in_session": True,
            "session": {
                "track": "Circuit de Test", "session_type": 10,
                "current_et": et, "end_et": END_ET, "max_laps": INT_MAX,
                "track_len": TL, "game_phase": 5, "yellow_state": 0,
                "sector_flags": [0, 0, 0], "pit_speed_limit": 80.0, "in_realtime": True,
                "raining": 0.0, "dark_cloud": 0.1, "ambient_temp": 22.0, "track_temp": 31.0,
                "avg_wetness": 0.0, "num_vehicles": len(vehicles),
            },
            "player": {
                "id": PLAYER, "lap_number": plaps + 1, "lap_start_et": plse,
                "speed_kmh": round(V * 3.6, 1), "rpm": 7200, "max_rpm": 9250, "gear": 5,
                "fuel": 60.0, "fuel_capacity": 90.0,
                "water_temp": 88.0, "oil_temp": 102.0,
                "overheating": False, "detached": False,
                "dent_severity": [0] * 8, "last_impact_et": 0.0, "last_impact_mag": 0.0,
                "in_pitlane": False, "speed_limiter": False,
                "wheels": [wheel(), wheel(), wheel(), wheel()],
            },
            "vehicles": vehicles,
        })
    return snaps


def obs_gaps(snaps, cid):
    """분석기가 보는 갭 — 스냅샷 lap_dist 차이를 [-L/2, L/2)로 (traffic.wrap_gap)."""
    out = []
    for s in snaps:
        me = next(v for v in s["vehicles"] if v["is_player"])
        v = next(v for v in s["vehicles"] if v["id"] == cid)
        out.append((s["t"], (v["lap_dist"] - me["lap_dist"] + TL / 2) % TL - TL / 2))
    return out


def first(gaps, start, pred, edge):
    """start 이후 pred가 처음 참인 틱 — 경계값과의 여유를 확인 (edge(g) = 경계까지 거리, 참이면 ≥0)."""
    for k in range(start, len(gaps)):
        if pred(gaps[k][1]):
            assert edge(gaps[k][1]) >= MARGIN_M and edge(gaps[k - 1][1]) <= -MARGIN_M, \
                f"경계에 너무 가까움: t={gaps[k][0]} g={gaps[k][1]}"
            return k
    raise AssertionError("조건이 오지 않음")


def expect_car(car, snaps):
    """차 하나의 기대 콜 — 규칙(문서 맨 위)대로 갭 궤적에서 구한다."""
    gaps = obs_gaps(snaps, car["id"])
    vr = car["vr"]
    thr = ALONG + min(vr * CALL_LAT, LEAD_MAX)
    raw, show = car["cls"], car["show"]
    ev = []

    def add(k, typ, data, voice):
        ev.append({"t": round(gaps[k][0], 2), "type": typ, "data": data, "voice": voice})

    if car["kind"] == "pass":
        k0 = next(k for k, (_, g) in enumerate(gaps) if g > -PARK + 1e-6)
        if car["faster"]:
            # 접근 예고: 50m 밖에서 도달 예상 10초 이내 (접근 속도 = vr, 생성기 등속)
            ka = first(gaps, k0, lambda g: g < -PROX and -g / vr <= ETA_WARN,
                       lambda g: (ETA_WARN - (-g / vr)) * vr)
            eta = -gaps[ka][1] / vr
            gap_sec = min(max(round(eta), 1), 6)
            gslot = f"{gap_sec} second" + ("s" if gap_sec > 1 else "")
            add(ka, "traffic_approach", {"cls": raw, "gap_sec": gap_sec},
                {"pool": "traffic_approach", "slots": {"cls": show, "gap": gslot}} if show
                else {"pool": "traffic_approach_any", "slots": {"gap": gslot}})
        kn = first(gaps, k0, lambda g: g >= -PROX, lambda g: g + PROX)
        add(kn, "traffic_close", {"pool": "nearby_behind", "cls": raw, "driver": car["driver"]},
            {"pool": "nearby_behind", "slots": {"cls": show}})
        kl = first(gaps, kn, lambda g: abs(g) <= thr, lambda g: thr - abs(g))
        add(kl, "traffic_close", {"pool": "alongside_right", "cls": raw, "driver": car["driver"]},
            {"pool": "alongside_right", "slots": {"cls": show}})
        kp = first(gaps, kl, lambda g: g > thr, lambda g: g - thr)
        add(kp, "traffic_update", {"pool": "pass_complete", "cls": raw},
            {"pool": "pass_complete", "slots": {"cls": show}})
    else:   # catch — 내가 앞 차를 잡는다
        k0 = next(k for k, (_, g) in enumerate(gaps) if g < PARK - 1e-6)
        kb = first(gaps, k0, lambda g: g <= PROX, lambda g: PROX - g)
        if car["backmarker"]:
            add(kb, "traffic_update", {"pool": "backmarker_ahead", "cls": raw},
                {"pool": "backmarker_ahead", "slots": {"cls": show}})
        kl = first(gaps, kb, lambda g: abs(g) <= thr, lambda g: thr - abs(g))
        add(kl, "traffic_close", {"pool": "alongside_right", "cls": raw, "driver": car["driver"]},
            {"pool": "alongside_right", "slots": {"cls": show}})
        kd = first(gaps, kl, lambda g: g < -PROX, lambda g: -PROX - g)
        add(kd, "traffic_update", {"pool": "dropped", "cls": raw},
            {"pool": "dropped", "slots": {"cls": show}})
    return ev


def build(name):
    sc = SCENARIOS[name]
    movers = [c for c in sc["cars"] if c["kind"] != "park"]
    n_ticks = tick(LEAD_S + len(movers) * SLOT_S) + 1
    snaps = build_snaps(sc, n_ticks)
    events, cases = [], []
    for c in movers:
        ev = expect_car(c, snaps)
        events += ev
        cases.append({"car": c["driver"], "cls": c["cls"], "me": sc["me"], "kind": c["kind"],
                      "faster": c["faster"], "backmarker": c.get("backmarker", False),
                      "show": c["show"], "calls": [e["data"].get("pool", e["type"]) for e in ev],
                      "note": c["note"], "old": c["old"]})
    for c in sc["cars"]:
        if c["kind"] == "park":
            cases.append({"car": c["driver"], "cls": c["cls"], "me": sc["me"], "kind": "park",
                          "calls": [], "note": c["note"], "old": None})
    events.sort(key=lambda e: e["t"])
    # 쿨다운 밖인지 (기대 콜끼리) — 그 규칙이 결과를 바꾸면 경우 설계가 틀린 것
    last = {}
    for e in events:
        cd, urgent_half = COOLDOWN[e["type"]]
        prio_critical = e["type"] != "traffic_update"
        if e["type"] in last:
            dt = e["t"] - last[e["type"]]
            assert dt >= cd or (urgent_half and prio_critical and dt >= cd / 2 + 0.1), \
                f"{name}: {e['type']} 쿨다운 안 ({dt:.2f}초)"
        last[e["type"]] = e["t"]
    sidecar = {
        "scenario": f"{PREFIX}_{name}",
        "note": "트래픽 클래스 판정 — 같은 클래스(문자열·계열)는 랩핑일 때만 빠른 차, 다른 계열은 서열, "
                "계열을 모르면 관측 페이스(2%), 그것도 없으면 빠른 차도 백마커도 아님. 모르는 클래스는 "
                "호칭 없이 말한다 (예전 \"faster car\")",
        "types": ["traffic_approach", "traffic_close", "traffic_update", "traffic_multi"],
        "events": events,
        "cases": cases,
    }
    return snaps, sidecar


def write(out_dir, name, snaps, sidecar):
    body = "".join(json.dumps(s, ensure_ascii=False, separators=(",", ":")) + "\n"
                   for s in snaps).encode("utf-8")
    base = os.path.join(out_dir, f"{PREFIX}_{name}")
    with open(base + ".jsonl.gz", "wb") as raw:
        # mtime=0·빈 파일명 — 같은 입력이면 같은 바이트 (zlib 버전 차이는 --check가 해제 후 비교)
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as f:
            f.write(body)
    with open(base + ".expect.json", "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(sidecar, indent=1, sort_keys=True, ensure_ascii=False) + "\n")
    return base + ".jsonl.gz", base + ".expect.json"


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
    rc = 0
    for name in SCENARIOS:
        snaps, sidecar = build(name)
        label = f"{PREFIX}_{name}"
        if not args.check:
            os.makedirs(args.out_dir, exist_ok=True)
            gz, side = write(args.out_dir, name, snaps, sidecar)
            print(f"{gz}: 스냅샷 {len(snaps)}개 (+ {os.path.basename(side)}, "
                  f"기대 콜 {len(sidecar['events'])}개 / 경우 {len(sidecar['cases'])}개)")
            continue
        with tempfile.TemporaryDirectory() as tmp:
            gz, side = write(tmp, name, snaps, sidecar)
            mine_gz = os.path.join(args.out_dir, label + ".jsonl.gz")
            mine_side = os.path.join(args.out_dir, label + ".expect.json")
            if not (os.path.exists(mine_gz) and os.path.exists(mine_side)):
                print(f"❌ {label}: 파일 없음 — 재생성 필요 (python3 tools/make_traffic_class_replay.py)")
                rc = 1
                continue
            with open(mine_side, "rb") as a, open(side, "rb") as b:
                same = read_gz(mine_gz) == read_gz(gz) and a.read() == b.read()
        if same:
            print(f"✅ {label}: 생성기 출력과 동일 (스냅샷 {len(snaps)}개)")
        else:
            print(f"❌ {label}: 생성기 출력과 다름 — 재생성 필요 (python3 tools/make_traffic_class_replay.py)")
            rc = 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
