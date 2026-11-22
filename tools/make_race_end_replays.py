"""
레이스 종료(체커) 회귀용 합성 리플레이 생성기 — 리플레이 + 정답(truth) 사이드카.

기존 리플레이(race_full, traffic2)는 레이스 중간에 끝나서 마지막 랩·체커 판단이 한 번도
재생되지 않았다. 여기서는 짧은 레이스 5개를 체커 이후까지 만들고, 규칙대로 계산한 정답을
data/replays/<이름>.truth.json에 같이 쓴다 (tools/race_end_oracle.py가 대조).
재시작 시나리오는 같은 레이스를 ET만 되감아 한 번 더 돌린다 (스냅샷 t는 계속 증가 —
라이브의 벽시계처럼). 정답은 레이스마다 "races" 목록에 들어간다.

체커 뒤 30초(시나리오에 따라 더)까지 돌려 체커 뒤 무전도 정답으로 적는다:
  finish.pool/start_place/gained  체커 콜 등급 — 스타트 순위는 그린 직전(페이즈 4) 마지막
                                  스냅샷의 클래스 순위, 중간 합류(start_t)면 0
  post                            체커 콜 +7초 쿨다운 랩(아직 달리는 차 수·빠른 클래스),
                                  +20초 요약(베스트 랩 클래스 순위), 피트인(리미터 경고 포함)
엔진 코드를 import하지 않고 이 파일의 규칙으로 따로 계산한다 (두 엔진이 같이 틀려도 잡히게).
<이름>.config.json(선택)은 리플레이 하니스가 읽는 설정 덮어쓰기 — 리포터를 켠다.

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
GREEN_ET = 3.0              # 페이즈 4(카운트다운) → 5(그린)
POST_TAIL_S = 30.0          # 체커 뒤 스냅샷 길이 (기본)
POST_COOLDOWN_S = 7.0       # 체커 콜 → 쿨다운 랩 콜
POST_SUMMARY_S = 20.0       # 체커 콜 → 요약 콜
CLASS_POS_MAX = 60
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


def snapshot(n, cars, player, E, M, flag, fin, wear_per_lap=0.008):
    et = round(n / HZ, 3)
    phase = 4 if et < GREEN_ET else (8 if et >= flag else 5)
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
    wear = round(max(1.0 - wear_per_lap * laps_frac, 0.0), 4)

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


# 클래스 서열 (빠를수록 큼) — 쿨다운 랩의 '빠른 클래스' 판정 정답용
CLASS_RANK = (("hyper", 4), ("lmh", 4), ("lmdh", 4), ("lmp2", 3), ("gte", 2), ("gt3", 1))


def class_rank(cls):
    c = (cls or "").lower()
    return next((r for k, r in CLASS_RANK if k in c), 0)


def class_place(snap, cid):
    vs = snap["vehicles"]
    me = next(v for v in vs if v["id"] == cid)
    return 1 + sum(1 for v in vs if v["cls"] == me["cls"] and 0 < v["place"] < me["place"])


def finish_tier(place, start, multiclass):
    """체커 콜 등급 정답 — (풀, 스타트, 올라온 계단)."""
    if not 1 <= place <= CLASS_POS_MAX:
        return "race_end_plain", 0, 0
    if not 1 <= start <= CLASS_POS_MAX:
        start = 0
    if place == 1:
        return ("race_end_class_win" if multiclass else "race_end_win"), start, \
            (start - place if start else 0)
    if place <= 3:
        return "race_end_podium", start, (start - place if start else 0)
    if start == 0:
        return "race_end_result", 0, 0
    gained = start - place
    if gained >= 2:
        return "race_end_gained", start, gained
    if gained <= -2:
        return "race_end_lost", start, gained
    return "race_end_held", start, gained


def post_truth(snaps_at, pf_tick, pid, multiclass, pit_entry=None):
    """체커 뒤 콜 정답 — 쿨다운 랩(+7), 요약(+20), 피트인(진입 틱, 리미터 경고 먼저)."""
    post = []
    t_cd = round(pf_tick + POST_COOLDOWN_S, 3)
    if pit_entry is None or pit_entry > t_cd:
        snap = snaps_at(t_cd)
        me = next(v for v in snap["vehicles"] if v["id"] == pid)
        racing = [v for v in snap["vehicles"]
                  if v["id"] != pid and v["finish_status"] == 0 and v["place"] > 0
                  and not v["in_pits"] and not v["in_garage"]]
        faster = any(class_rank(v["cls"]) > class_rank(me["cls"]) for v in racing)
        pool = ("cooldown_lap_faster" if faster
                else "cooldown_lap_traffic" if racing else "cooldown_lap")
        post.append({"t": round(t_cd, 2), "type": "cooldown_lap", "pool": pool,
                     "still_racing": len(racing)})
    t_sum = round(pf_tick + POST_SUMMARY_S, 3)
    snap = snaps_at(t_sum)
    me = next(v for v in snap["vehicles"] if v["id"] == pid)
    same = [v for v in snap["vehicles"] if v["cls"] == me["cls"]]
    timed = sum(1 for v in same if v["best_lap"] > 0)
    rank = 0 if me["best_lap"] <= 0 else 1 + sum(
        1 for v in same if v["id"] != pid and 0 < v["best_lap"] < me["best_lap"])
    sfx = "_class" if multiclass else ""
    pool = ("race_summary_fastest" + sfx if rank == 1 and timed >= 2
            else "race_summary_pace" + sfx if rank in (2, 3) else "race_summary_debrief")
    post.append({"t": round(t_sum, 2), "type": "race_summary", "pool": pool, "rank": rank})
    if pit_entry is not None:
        post.append({"t": round(pit_entry, 2), "type": "pit_limiter", "pool": "pit_limiter"})
        post.append({"t": round(pit_entry, 2), "type": "post_race_pit", "pool": "post_race_pit"})
    post.sort(key=lambda p: p["t"])        # 안정 정렬 — 같은 틱이면 리미터 → 피트인
    return post


def me_car(cid=0, overrides=None):
    return Car(cid, "나", "Porsche 911 GT3 R", GT3, 0.0, 80.0, overrides=overrides,
               is_player=True)


def lead_cars():
    return [me_car(),
            Car(1, "리바이", "Ferrari 296 GT3", GT3, -160.0, 80.6),
            Car(2, "헌터", "McLaren 720S GT3", GT3, -330.0, 81.2)]


def gained_cars():
    """멀티클래스 GT3 9대 + 하이퍼카 2대 — 클래스 9위 스타트, 내가 가장 빨라 앞 4대를 잡는다.
    하이퍼카 하나는 내 체커 뒤에도 달린다 (쿨다운 랩 '빠른 차' 경고)."""
    me = me_car()
    me.lap_s = 79.0
    cars = [me]
    rows = (("리바이", 30.0, 81.6), ("헌터", 60.0, 81.2), ("로키", 90.0, 80.8),
            ("토르", 120.0, 80.4), ("비전", 500.0, 80.0), ("완다", 560.0, 80.2),
            ("스톰", 620.0, 80.4), ("블랙", 680.0, 80.6))
    for i, (drv, off, pace) in enumerate(rows):
        cars.append(Car(i + 1, drv, "Ferrari 296 GT3", GT3, off, pace))
    cars.append(Car(9, "토요타7", "Toyota GR010", HY, 1500.0, 68.0))
    cars.append(Car(10, "페라리50", "Ferrari 499P", HY, -300.0, 70.0))
    return cars


def held_cars():
    """단일 클래스 6대 — 5위 스타트 5위 피니시, 뒤차는 중간에 리타이어. 내 베스트 랩(3랩 79.3)은
    클래스 2번째 (1위 79.0)."""
    return [me_car(overrides={3: 79.3}),
            Car(1, "리바이", "Ferrari 296 GT3", GT3, 400.0, 80.0, overrides={2: 79.0}),
            Car(2, "헌터", "McLaren 720S GT3", GT3, 300.0, 80.0),
            Car(3, "로키", "Aston Vantage GT3", GT3, 200.0, 80.0),
            Car(4, "토르", "BMW M4 GT3", GT3, 100.0, 80.0),
            Car(5, "비전", "Lexus RC F GT3", GT3, -100.0, 80.0, retire_t=300.0)]


def lost_cars():
    """단일 클래스 14대 — 4위 스타트, 랩당 2초 느려 12위 피니시 (예전 8위 클램프 회귀)."""
    me = me_car()
    me.lap_s = 82.0
    cars = [me,
            Car(1, "리바이", "Ferrari 296 GT3", GT3, 300.0, 80.0),
            Car(2, "헌터", "McLaren 720S GT3", GT3, 200.0, 80.0),
            Car(3, "로키", "Aston Vantage GT3", GT3, 100.0, 80.0)]
    names = ("토르", "비전", "완다", "스톰", "블랙", "팔콘", "윈터", "샘", "페퍼", "해피")
    offs = (-20.0, -40.0, -60.0, -80.0, -100.0, -120.0, -140.0, -160.0, -420.0, -470.0)
    for i, (drv, off) in enumerate(zip(names, offs)):
        cars.append(Car(4 + i, drv, "Porsche 911 GT3 R", GT3, off, round(80.0 + 0.1 * i, 1)))
    return cars


def midjoin_cars():
    """멀티클래스 — 앱이 그린 뒤 150초에 붙는다 (스타트 순위 모름). 클래스 5위."""
    return [me_car(),
            Car(1, "리바이", "Ferrari 296 GT3", GT3, 400.0, 79.8),
            Car(2, "헌터", "McLaren 720S GT3", GT3, 300.0, 80.1),
            Car(3, "로키", "Aston Vantage GT3", GT3, 200.0, 80.2),
            Car(4, "토르", "BMW M4 GT3", GT3, 100.0, 80.3),
            Car(5, "비전", "Lexus RC F GT3", GT3, -300.0, 80.8),
            Car(6, "토요타7", "Toyota GR010", HY, 900.0, 68.0)]


def pitin_cars():
    """단일 클래스 4대 — 피니시 랩만 1.5초 느리다 (페이스 콜이 나가면 안 된다)."""
    return [me_car(overrides={6: 81.5}),
            Car(1, "리바이", "Ferrari 296 GT3", GT3, 300.0, 80.1),
            Car(2, "헌터", "McLaren 720S GT3", GT3, 150.0, 80.2),
            Car(3, "로키", "Aston Vantage GT3", GT3, -200.0, 80.4)]


def pitin_tweak(snap, et, ctx):
    """체커 뒤 스크립트 (피니시 틱 기준 초) — 레이스 중이면 콜이 나갈 일을 차례로 겪는다.
    피니시 뒤엔 쿨다운·요약·리미터·피트인만 나가야 한다 (오라클 O5/O6)."""
    dt = round(et - ctx["pf_tick"], 3)
    if dt < 0:
        return
    me = next(v for v in snap["vehicles"] if v["is_player"])
    p = snap["player"]
    if 30.0 <= dt < 33.0:
        me["flag_blue"] = True                      # 블루 플래그
    if dt >= 35.0:
        me["num_penalties"] = 1                     # 페널티 (소화할 수 없다)
    if dt >= 38.0:
        p["last_impact_et"] = round(ctx["pf_tick"] + 38.0, 3)   # 큰 충격 (데미지·점검 리포트)
        p["last_impact_mag"] = 3000.0
        p["dent_severity"] = [0, 0, 2, 0, 0, 0, 0, 0]
    if dt >= 45.0:
        p["in_pitlane"] = True                      # 피트레인 진입 — 1초 동안 리미터 꺼짐
        me["in_pits"] = True
        if dt < 46.0:
            p["speed_kmh"], p["speed_limiter"] = 95.0, False
        else:
            p["speed_kmh"], p["speed_limiter"] = 60.0, True
    if dt >= 55.0:
        # 피트레인 안에서 타이밍 라인 통과 — 체커 뒤 랩이 하나 더 잡혀도 스틴트·순위·리포트 없음
        me["total_laps"] = ctx["pf_k"] + 1
        me["last_lap"] = 135.0
        me["lap_start_et"] = round(ctx["pf_tick"] + 55.0, 3)
        me["time_into_lap"] = round(dt - 55.0, 3)
        p["lap_number"] = ctx["pf_k"] + 2
        p["lap_start_et"] = me["lap_start_et"]
    if dt >= 70.0:
        me["in_garage"] = True


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
    ("race_end_gained", "멀티클래스, 클래스 9위 스타트 → 5위 (4계단 상승) — 체커 뒤 하이퍼카가 아직 달린다",
     443.0, INT_MAX, gained_cars, "crossing", 1),
    ("race_end_held", "단일 클래스 5위 스타트 → 5위, 뒤차 리타이어 — 체커 뒤 달리는 차 없음, 베스트 랩 2번째",
     443.0, INT_MAX, held_cars, "crossing", 1),
    ("race_end_lost", "단일 클래스 14대, 4위 스타트 → 12위 — 실제 순위를 말한다 (8위로 자르지 않는다)",
     443.0, INT_MAX, lost_cars, "crossing", 1),
    ("race_end_midjoin", "그린 뒤 150초에 앱 시작 (중간 합류 브리핑) — 스타트 순위를 몰라 결과만",
     443.0, INT_MAX, midjoin_cars, "crossing", 2),
    ("race_end_pitin", "체커 뒤 쿨다운 → 블루·페널티·충격(침묵) → 피트인(리미터 경고) → 피트레인 라인 통과 "
     "→ 차고. 리포터 켬 (설정 사이드카)",
     443.0, INT_MAX, pitin_cars, "crossing", 1),
]

RESTART_GAP_S = 30.0        # 재시작 전 공백 (스냅샷 없음)

# 시나리오별 추가 설정 (없으면 기본):
#   start_t          첫 스냅샷 시각 — 그린 뒤면 중간 합류 (스타트 순위 모름)
#   post_tail_s      체커 뒤 스냅샷 길이
#   cooldown_factor  체커 뒤 내 페이스 배율 (0.6 = 쿨다운 랩)
#   wear_per_lap     타이어 마모 (랩당 잔량 감소)
#   tweak            스냅샷 후처리 (snap, et, ctx) — 체커 뒤 피트인·플래그·충격 등
#   pit_entry        체커 뒤 피트레인 진입 시각 (피니시 틱 기준 초) — 정답에 리미터·피트인
#   config           <이름>.config.json — 하니스 설정 덮어쓰기 (리포터)
#   expect           생성 결과가 의도한 시나리오인지 확인 (pool/start_place/class_place/gained)
#   expect_post      체커 뒤 콜 (type, pool) 목록 확인
EXTRA: dict = {
    "race_end_gained": {"expect": {"pool": "race_end_gained", "start_place": 9, "class_place": 5,
                                   "gained": 4},
                        "expect_post": [("cooldown_lap", "cooldown_lap_faster"),
                                        ("race_summary", "race_summary_fastest_class")]},
    "race_end_held": {"expect": {"pool": "race_end_held", "start_place": 5, "class_place": 5},
                      "expect_post": [("cooldown_lap", "cooldown_lap"),
                                      ("race_summary", "race_summary_pace")]},
    "race_end_lost": {"expect": {"pool": "race_end_lost", "start_place": 4, "class_place": 12},
                      "expect_post": [("cooldown_lap", "cooldown_lap_traffic"),
                                      ("race_summary", "race_summary_debrief")]},
    "race_end_midjoin": {"start_t": 150.0,
                         "expect": {"pool": "race_end_result", "start_place": 0,
                                    "class_place": 5}},
    "race_end_pitin": {"cooldown_factor": 0.6, "wear_per_lap": 0.12, "tweak": pitin_tweak,
                       "pit_entry": 45.0, "post_tail_s": 75.0,
                       "config": {"reports.laptime_every_lap": True,
                                  "reports.status_every_laps": 2},
                       "expect": {"pool": "race_end_podium", "class_place": 3},
                       "expect_post": [("cooldown_lap", "cooldown_lap_traffic"),
                                       ("race_summary", "race_summary_fastest"),
                                       ("pit_limiter", "pit_limiter"),
                                       ("post_race_pit", "post_race_pit")]},
}


def build(name, note, E, M, mk, kind, exact_from):
    if kind == "restart":
        return build_restart(name, note, E, M, mk, exact_from)
    opts = EXTRA.get(name, {})
    cars = mk()
    for c in cars:
        c.build(1200.0)
    player = next(c for c in cars if c.is_player)
    flag, flag_car, fin, leader_cross, _ = truth(cars, E, M)
    pf_t, pf_k = fin[player.id]
    if opts.get("cooldown_factor"):
        # 체커 뒤 쿨다운 랩 — 내 피니시 뒤 랩만 느리게 (체커·피니시 판단은 그대로)
        for k in range(pf_k + 1, pf_k + 6):
            player.over[k] = player.lap_s / opts["cooldown_factor"]
        player.build(1200.0)
        again = truth(cars, E, M)
        assert again[0] == flag and again[2] == fin, f"{name}: 쿨다운 페이스가 체커를 바꿨다"
    flag_tick = tick_of(flag)
    pf_tick = tick_of(pf_t)
    pcross = [(t, k) for (t, k) in player.cross if k >= 1 and t <= pf_t]
    start_t = opts.get("start_t", 0.0)

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

    tweak = opts.get("tweak")
    ctx = {"pf_tick": pf_tick, "pf_k": pf_k, "player": player}
    snaps = []
    n = int(round(start_t * HZ))
    n0 = n
    end = pf_tick + opts.get("post_tail_s", POST_TAIL_S)
    while round(n / HZ, 3) <= end + 1e-9:
        snap = snapshot(n, cars, player, E, M, flag, fin, opts.get("wear_per_lap", 0.008))
        if tweak is not None:
            tweak(snap, snap["t"], ctx)
        snaps.append(snap)
        n += 1

    def at(t):
        return snaps[int(round(t * HZ)) - n0]

    def lap_at(t):
        return next(v for v in at(t)["vehicles"] if v["is_player"])["total_laps"]

    crossings = [{"t": round(tick_of(t), 2), "lap": k, "laps_left": pf_k - k}
                 for (t, k) in pcross if t >= start_t]
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
    cp = class_place(fsnap, player.id)
    multiclass = len({c.cls for c in cars}) > 1
    # 스타트 순위 — 그린 직전 마지막 스냅샷 (중간 합류면 그린 전 스냅샷이 없다)
    pre = [sn for sn in snaps if sn["t"] < GREEN_ET]
    start = class_place(pre[-1], player.id) if pre else 0
    pool, start, gained = finish_tier(cp, start, multiclass)
    if "expect" in opts:
        want = opts["expect"]
        got = {"pool": pool, "start_place": start, "class_place": cp, "gained": gained}
        assert all(got[k] == v for k, v in want.items()), f"{name}: 목표 결과와 다름 {got} ≠ {want}"
    sidecar = {
        "scenario": name,
        "note": note,
        "end_et": E,
        "max_laps": M,
        "leader_flag_t": round(flag_tick, 2),
        "crossings": crossings,
        "trace_exact_from_lap": exact_from,
        "final_calls": final_calls,
        "finish": {"t": round(pf_tick, 2), "lap": pf_k, "class_place": cp,
                   "pool": pool, "start_place": start, "gained": gained},
        "post": post_truth(at, pf_tick, player.id, multiclass,
                           opts.get("pit_entry") and round(pf_tick + opts["pit_entry"], 3)),
    }
    if "expect_post" in opts:
        got = [(p["type"], p["pool"]) for p in sidecar["post"]]
        assert got == opts["expect_post"], f"{name}: 체커 뒤 콜 목표와 다름 {got}"
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
        r = {k: race[k] for k in ("crossings", "trace_exact_from_lap", "final_calls", "finish",
                                  "post")}
        r = json.loads(json.dumps(r))
        for c in r["crossings"] + r["final_calls"] + [r["finish"]] + r["post"]:
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
    files = [side]
    cfg = EXTRA.get(name, {}).get("config")
    if cfg is not None:
        path = os.path.join(out_dir, name + ".config.json")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(json.dumps(cfg, indent=1, sort_keys=True) + "\n")
        files.append(path)
    return gz, files


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
        for name, n, gz, files in generate(args.out_dir, args.only):
            print(f"{gz}: 스냅샷 {n}개 (+ {', '.join(os.path.basename(f) for f in files)})")
        return 0

    bad = 0
    with tempfile.TemporaryDirectory() as tmp:
        for name, n, gz, files in generate(tmp, args.only):
            mine_gz = os.path.join(args.out_dir, name + ".jsonl.gz")
            mine = [os.path.join(args.out_dir, os.path.basename(f)) for f in files]
            # 설정 사이드카는 생성기가 쓰는 시나리오에만 — 남은 옛 파일도 불일치
            stale = os.path.join(args.out_dir, name + ".config.json")
            if os.path.exists(stale) and stale not in mine:
                mine.append(stale)
                files = files + [os.path.join(tmp, "__없음__")]
            if not all(os.path.exists(f) for f in [mine_gz] + mine):
                print(f"❌ {name}: 파일 없음 — 재생성 필요")
                bad = 1
                continue
            same = read_gz(mine_gz) == read_gz(gz)
            for a_path, b_path in zip(mine, files):
                if not os.path.exists(b_path):
                    same = False
                    continue
                with open(a_path, "rb") as a, open(b_path, "rb") as b:
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
