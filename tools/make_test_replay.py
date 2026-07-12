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

# Windows 콘솔(cp949) 인코딩 가드
import sys
if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError):
        pass

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
                 drift_per_lap=0.0, is_player=False, pit_lap=None):
        self.id = cid
        self.driver = driver
        self.vehicle = vehicle
        self.cls = cls
        self.base_lap = base_lap
        self.start_offset = start_offset_m
        self.drift = drift_per_lap      # 랩마다 랩타임 변화 (음수=빨라짐)
        self.is_player = is_player
        self.pit_lap = pit_lap          # 이 랩을 마치면 25초 피트 (라이벌 피트 테스트)
        self.pit_until = -1.0
        self.pits_made = 0
        self.laps_done = 0
        self.last_lap_time = 0.0
        self.best_lap_time = 0.0
        self.lap_start_et = 0.0
        self.total_dist = start_offset_m
        self._rng = random.Random(cid * 7919)

    def in_pits(self, et: float) -> bool:
        return et < self.pit_until

    def lap_time_at(self, lap_no: int) -> float:
        t = self.base_lap + self.drift * lap_no + self._rng.uniform(-0.4, 0.4)
        # 플레이어: 8랩 이후 타이어 마모로 페이스 드랍
        if self.is_player and lap_no >= 8:
            t += 0.9 * (lap_no - 7)
        return t

    def advance(self, dt: float, et: float):
        cur_lap_time = self.lap_time_at(self.laps_done)
        speed = TRACK_LEN / cur_lap_time  # m/s (등속 근사)
        if self.in_pits(et):
            speed *= 0.35                 # 피트레인 서행
        self.total_dist += speed * dt
        new_laps = int(self.total_dist // TRACK_LEN)
        if new_laps > self.laps_done:
            if self.pit_lap is not None and new_laps == self.pit_lap \
                    and self.pit_until < 0:
                self.pit_until = et + 25.0
                self.pits_made += 1
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


def build_cars(scenario: str = "race") -> list[SimCar]:
    if scenario == "traffic":
        # 다중 차량 트래픽 검증용: 하이퍼카 2대 줄지어 접근(묶음 콜),
        # GT3 한 대 천천히 접근→나란히→추월(상태 전이 서사),
        # GT3 한 대 붙었다가 떨어짐(dropped 콜)
        return [
            SimCar(0, "나", "Porsche 911 GT3 R", GT3_CLASS, PLAYER_LAP, 0.0, is_player=True),
            SimCar(1, "토요타7", "Toyota GR010", HYPER_CLASS, HYPER_LAP, -600.0),
            SimCar(2, "토요타8", "Toyota GR010", HYPER_CLASS, HYPER_LAP + 0.1, -690.0),
            SimCar(3, "리바이", "Ferrari 296 GT3", GT3_CLASS, PLAYER_LAP - 1.4, -350.0),
            SimCar(4, "헌터", "McLaren 720S GT3", GT3_CLASS, PLAYER_LAP - 0.9, -180.0,
                   drift_per_lap=0.45),   # 처음엔 접근하다 점점 느려져 떨어짐
            SimCar(5, "스톨", "Aston Vantage GT3", GT3_CLASS, 2100.0, 450.0),
            # ↑ 사실상 정지(2m/s) — 고스트/사고 차량 필터 테스트: 배틀 콜 없이 위험 안내만
        ]
    return [
        SimCar(0, "나", "Porsche 911 GT3 R", GT3_CLASS, PLAYER_LAP, 0.0, is_player=True),
        SimCar(1, "리바이", "Ferrari 296 GT3", GT3_CLASS, PLAYER_LAP - 0.2, 180.0,
               drift_per_lap=-0.15, pit_lap=6),   # 앞에서 달리다 6랩 마치고 피트 (언더컷 테스트)
        SimCar(2, "헌터", "McLaren 720S GT3", GT3_CLASS, PLAYER_LAP + 1.2, -220.0,
               drift_per_lap=-0.35),   # 뒤에서 출발, 점점 빨라져 접근 → 갭 코멘트 테스트
        SimCar(3, "토요타7", "Toyota GR010", HYPER_CLASS, HYPER_LAP, 900.0),
        SimCar(4, "페라리50", "Ferrari 499P", HYPER_CLASS, HYPER_LAP + 0.5, 2500.0),
    ]


def phase_at(et: float, scenario: str) -> tuple[int, int, list]:
    """(game_phase, yellow_state, sector_flags). race 시나리오에 FCY/옐로 구간 삽입."""
    if scenario != "race":
        return 5, 0, [0, 0, 0]
    if et < 3.0:
        return 4, 0, [0, 0, 0]              # 카운트다운 → 그린 (레이스 스타트 콜)
    if 300.0 <= et < 320.0:
        return 5, 0, [0, 1, 0]              # 섹터2 로컬 옐로
    if 610.0 <= et < 690.0:                 # 플레이어 5랩째쯤 FCY 80초
        yellow = 2 if et < 650.0 else 4     # 전반 피트 클로즈 → 후반 오픈
        return 6, yellow, [1, 1, 1]
    return 5, 0, [0, 0, 0]


def path_lat_of(c: SimCar, player: SimCar) -> float:
    """추월 구간(±40m)에서는 옆으로 비켜 나란히 지나가는 것처럼 시뮬레이션."""
    if c.is_player:
        return 0.0
    half = TRACK_LEN / 2
    gap = (c.total_dist - player.total_dist + half) % TRACK_LEN - half
    if abs(gap) < 40.0:
        return 2.8 if c.id % 2 else -2.8    # 차량별로 왼/오른쪽 고정
    return ((c.id * 37) % 10) / 10 - 0.5    # 평소엔 라인 미세 편차


def make_snapshot(t: float, et: float, cars: list[SimCar], player: SimCar,
                  scenario: str = "race") -> dict:
    game_phase, yellow_state, sector_flags = phase_at(et, scenario)
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
            "path_lat": round(path_lat_of(c, player), 2),
            "sector": 1 + int(c.lap_dist / TRACK_LEN * 3) % 3,
            "last_lap": round(c.last_lap_time, 3),
            "best_lap": round(c.best_lap_time, 3),
            "last_s1": round(c.last_lap_time / 3, 3) if c.last_lap_time else 0.0,
            "last_s2": round(c.last_lap_time * 2 / 3, 3) if c.last_lap_time else 0.0,
            "time_behind_next": round(tbn, 3),
            "laps_behind_next": 0,
            "time_behind_leader": round(tbl, 3),
            "in_pits": c.in_pits(et), "pit_state": 3 if c.in_pits(et) else 0,
            "num_pitstops": c.pits_made,
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

    # 3랩째 중반에 접촉 발생 시뮬레이션 (프론트 우측, 페이스 영향 없음 시나리오)
    impact_et = 3 * PLAYER_LAP + 40.0
    hit = scenario == "race" and et >= impact_et

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
        "dent_severity": [0, 1, 0, 0, 0, 0, 0, 0] if hit else [0] * 8,
        "last_impact_et": round(impact_et, 1) if hit else 0.0,
        "last_impact_mag": 850.0 if hit else 0.0,
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
            "game_phase": game_phase,
            "yellow_state": yellow_state,
            "sector_flags": sector_flags,
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output")
    parser.add_argument("--laps", type=int, default=12, help="플레이어 기준 랩 수")
    parser.add_argument("--hz", type=float, default=5.0, help="스냅샷 주기")
    parser.add_argument("--scenario", default="race", choices=["race", "traffic"],
                        help="race=기본 레이스, traffic=다중 차량 트래픽 검증")
    args = parser.parse_args()

    os.makedirs(os.path.dirname(args.output) or ".", exist_ok=True)
    cars = build_cars(args.scenario)
    player = cars[0]
    dt = 1.0 / args.hz
    et = 0.0
    n = 0
    with open(args.output, "w", encoding="utf-8") as f:
        while player.laps_done < args.laps:
            for c in cars:
                c.advance(dt, et)
            snap = make_snapshot(et, et, cars, player, args.scenario)
            f.write(json.dumps(snap, ensure_ascii=False, separators=(",", ":")) + "\n")
            et += dt
            n += 1
    print(f"{args.output}: 스냅샷 {n}개, 플레이어 {player.laps_done}랩, {et:.0f}초 분량")


if __name__ == "__main__":
    main()
