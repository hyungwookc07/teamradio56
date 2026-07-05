"""
합성 텔레메트리 리플레이 생성기.

실제 게임 녹화가 없어도 전체 파이프라인(--replay)을 테스트할 수 있도록
멀티클래스 레이스(LMGT3 플레이어 + 하이퍼카)를 시뮬레이션해서 JSONL로 저장한다.

시나리오 (기본 12랩, 트랙 4.2km):
  - 플레이어(LMGT3, P4 부근): 랩당 연료 2.8L, 우측 앞 타이어 과열 추세
  - 같은 클래스 라이벌 2대: 한 대는 서서히 접근, 한 대는 멀어짐
  - 하이퍼카 2대: 주기적으로 플레이어를 랩핑 (트래픽 콜 테스트)
  - 8랩째 페이스 드랍 (타이어 마모), 연료는 자연 감소로 경고 구간 진입

사용법: python tools/make_test_replay.py data/test.jsonl [--laps 12] [--hz 5]
"""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

TRACK_LEN = 4200.0          # m
PLAYER_LAP = 122.0          # 초
HYPER_LAP = 103.0
GT3_CLASS = "LMGT3"
HYPER_CLASS = "Hypercar"


class SimCar:
    def __init__(self, cid, driver, vehicle, cls, base_lap, start_offset_m,
                 drift_per_lap=0.0, is_player=False):
        self.id = cid
        self.driver = driver
        self.vehicle = vehicle
        self.cls = cls
        self.base_lap = base_lap
        self.start_offset = start_offset_m
        self.drift = drift_per_lap      # 랩마다 랩타임 변화 (음수=빨라짐)
        self.is_player = is_player
        self.laps_done = 0
        self.last_lap_time = 0.0
        self.best_lap_time = 0.0
        self.lap_start_et = 0.0
        self.total_dist = start_offset_m
        self._rng = random.Random(cid * 7919)

    def lap_time_at(self, lap_no: int) -> float:
        t = self.base_lap + self.drift * lap_no + self._rng.uniform(-0.4, 0.4)
        # 플레이어: 8랩 이후 타이어 마모로 페이스 드랍
        if self.is_player and lap_no >= 8:
            t += 0.9 * (lap_no - 7)
        return t

    def advance(self, dt: float, et: float):
        cur_lap_time = self.lap_time_at(self.laps_done)
        speed = TRACK_LEN / cur_lap_time  # m/s (등속 근사)
        self.total_dist += speed * dt
        new_laps = int(self.total_dist // TRACK_LEN)
        if new_laps > self.laps_done:
            self.last_lap_time = cur_lap_time
            if self.best_lap_time <= 0 or cur_lap_time < self.best_lap_time:
                self.best_lap_time = cur_lap_time
            self.laps_done = new_laps
            self.lap_start_et = et

    @property
    def lap_dist(self) -> float:
        return self.total_dist % TRACK_LEN

    @property
    def speed_ms(self) -> float:
        return TRACK_LEN / self.lap_time_at(self.laps_done)


def build_cars() -> list[SimCar]:
    return [
        SimCar(0, "나", "Porsche 911 GT3 R", GT3_CLASS, PLAYER_LAP, 0.0, is_player=True),
        SimCar(1, "리바이", "Ferrari 296 GT3", GT3_CLASS, PLAYER_LAP - 0.2, 180.0,
               drift_per_lap=-0.15),   # 앞에서 출발, 서서히 더 빨라짐 → 갭 벌어짐
        SimCar(2, "헌터", "McLaren 720S GT3", GT3_CLASS, PLAYER_LAP + 1.2, -220.0,
               drift_per_lap=-0.35),   # 뒤에서 출발, 점점 빨라져 접근 → 갭 코멘트 테스트
        SimCar(3, "토요타7", "Toyota GR010", HYPER_CLASS, HYPER_LAP, 900.0),
        SimCar(4, "페라리50", "Ferrari 499P", HYPER_CLASS, HYPER_LAP + 0.5, 2500.0),
    ]


def make_snapshot(t: float, et: float, cars: list[SimCar], player: SimCar) -> dict:
    # 순위: 총 주행거리 내림차순 (전 클래스 통합)
    ranked = sorted(cars, key=lambda c: -c.total_dist)
    place = {c.id: i + 1 for i, c in enumerate(ranked)}

    vehicles = []
    for c in cars:
        # 바로 앞 순위 차와의 시간 갭 (거리/속도 근사)
        tbn = 0.0
        p = place[c.id]
        if p > 1:
            ahead = ranked[p - 2]
            tbn = max(ahead.total_dist - c.total_dist, 0.0) / c.speed_ms
        leader = ranked[0]
        tbl = max(leader.total_dist - c.total_dist, 0.0) / c.speed_ms
        vehicles.append({
            "id": c.id, "driver": c.driver, "vehicle": c.vehicle, "cls": c.cls,
            "is_player": c.is_player, "place": p,
            "total_laps": c.laps_done,
            "lap_dist": round(c.lap_dist, 1),
            "sector": 1 + int(c.lap_dist / TRACK_LEN * 3) % 3,
            "last_lap": round(c.last_lap_time, 3),
            "best_lap": round(c.best_lap_time, 3),
            "last_s1": round(c.last_lap_time / 3, 3) if c.last_lap_time else 0.0,
            "last_s2": round(c.last_lap_time * 2 / 3, 3) if c.last_lap_time else 0.0,
            "time_behind_next": round(tbn, 3),
            "laps_behind_next": 0,
            "time_behind_leader": round(tbl, 3),
            "in_pits": False, "pit_state": 0, "num_pitstops": 0,
            "num_penalties": 0, "finish_status": 0,
            "flag_blue": (c.is_player and any(
                0 < (h.total_dist - c.total_dist) % (TRACK_LEN * 99) and
                0 < ((c.lap_dist - h.lap_dist) % TRACK_LEN) < 300
                for h in cars if h.cls == HYPER_CLASS)),
            "estimated_lap": round(c.base_lap, 3),
            "time_into_lap": round(c.lap_dist / c.speed_ms, 3),
            "lap_start_et": round(c.lap_start_et, 3),
            "pos": [round(math.cos(c.lap_dist / TRACK_LEN * 2 * math.pi) * 600, 1), 0.0,
                    round(math.sin(c.lap_dist / TRACK_LEN * 2 * math.pi) * 600, 1)],
            "in_garage": False,
        })

    laps_frac = player.total_dist / TRACK_LEN
    fuel = max(58.0 - 2.8 * laps_frac, 0.0)
    wear_fl = max(1.0 - 0.018 * laps_frac, 0.0)
    wear_fr = max(1.0 - 0.026 * laps_frac, 0.0)   # 우측 앞이 빨리 닳음
    wear_r = max(1.0 - 0.015 * laps_frac, 0.0)
    fr_temp = 92.0 + laps_frac * 1.6              # 우측 앞 과열 추세

    def wheel(base_temp, wear):
        return {
            "brake_temp": round(320 + 40 * math.sin(et / 7), 1),
            "pressure": 172.0,
            "temps": [round(base_temp - 3, 1), round(base_temp, 1), round(base_temp + 3, 1)],
            "carcass_temp": round(base_temp - 6, 1),
            "wear": round(wear, 4),
            "flat": False, "detached": False,
        }

    player_tele = {
        "id": player.id,
        "lap_number": player.laps_done + 1,
        "lap_start_et": round(player.lap_start_et, 3),
        "speed_kmh": round(player.speed_ms * 3.6 * (0.85 + 0.3 * abs(math.sin(et / 5))), 1),
        "rpm": round(6500 + 2200 * abs(math.sin(et / 2.2))),
        "max_rpm": 9250,
        "gear": 4,
        "fuel": round(fuel, 2),
        "fuel_capacity": 90.0,
        "water_temp": 88.0, "oil_temp": 102.0,
        "overheating": False, "detached": False,
        "dent_severity": [0] * 8,
        "last_impact_et": 0.0, "last_impact_mag": 0.0,
        "in_pitlane": False, "speed_limiter": False,
        "wheels": [wheel(88.0, wear_fl), wheel(fr_temp, wear_fr),
                   wheel(85.0, wear_r), wheel(86.0, wear_r)],
    }

    return {
        "t": round(t, 3),
        "connected": True,
        "in_session": True,
        "session": {
            "track": "Circuit de Test",
            "session_type": 10,          # race
            "current_et": round(et, 3),
            "end_et": 3600.0,
            "max_laps": 2147483647,
            "track_len": TRACK_LEN,
            "game_phase": 5,             # green flag
            "yellow_state": 0,
            "in_realtime": True,
            "raining": 0.0, "dark_cloud": 0.1,
            "ambient_temp": 22.0, "track_temp": 31.0,
            "avg_wetness": 0.0,
            "num_vehicles": len(cars),
        },
        "player": player_tele,
        "vehicles": vehicles,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output")
    parser.add_argument("--laps", type=int, default=12, help="플레이어 기준 랩 수")
    parser.add_argument("--hz", type=float, default=5.0, help="스냅샷 주기")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    cars = build_cars()
    player = cars[0]
    dt = 1.0 / args.hz
    et = 0.0
    n = 0
    with open(args.output, "w", encoding="utf-8") as f:
        while player.laps_done < args.laps:
            for c in cars:
                c.advance(dt, et)
            snap = make_snapshot(et, et, cars, player)
            f.write(json.dumps(snap, ensure_ascii=False, separators=(",", ":")) + "\n")
            et += dt
            n += 1
    print(f"{args.output}: 스냅샷 {n}개, 플레이어 {player.laps_done}랩, {et:.0f}초 분량")


if __name__ == "__main__":
    main()
