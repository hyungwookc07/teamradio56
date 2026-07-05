"""
세션 상태 + 랩 히스토리.

분석기는 "지금 값"이 아니라 여기 축적된 추세를 본다. 레이스가 끝나면
전체 히스토리를 JSON으로 저장해 복기/리플레이 분석에 쓴다.
"""

from __future__ import annotations

import json
import logging
import os
import time
from dataclasses import dataclass, field, asdict
from typing import Optional

from telemetry import Snapshot

log = logging.getLogger("state")

RACE_SESSION_MIN = 10   # mSession 10-13 = race


@dataclass
class LapRecord:
    lap_number: int              # 완료한 랩 번호 (1부터)
    lap_time: float
    s1: float
    s2: float                    # 원시 값은 S1+S2 누적이지만 여기엔 구간값으로 저장
    s3: float
    place: int
    fuel_left: float
    fuel_used: float             # 이 랩에서 쓴 연료 (피트 급유 랩은 -1)
    gap_ahead: float             # 랩 완료 시점 앞차와 갭 (없으면 -1)
    gap_behind: float
    tyre_wear: list = field(default_factory=list)      # FL FR RL RR 남은 수명 비율
    tyre_temps: list = field(default_factory=list)     # FL FR RL RR 캐리커스 온도 C
    brake_temps: list = field(default_factory=list)
    in_pits: bool = False        # 이 랩에 피트를 지났는가
    track_temp: float = 0.0
    raining: float = 0.0
    valid: bool = True           # 분석(평균 등)에 쓸 수 있는 랩인가


class SessionState:
    """현재 세션의 축적 상태. 세션이 바뀌면 reset."""

    def __init__(self):
        self.reset()

    def reset(self) -> None:
        self.session_type: Optional[int] = None
        self.track: str = ""
        self.track_len: float = 0.0
        self.laps: list[LapRecord] = []
        self.player_class: str = ""
        self.driver_name: str = ""
        self.total_vehicles: int = 0
        self.class_vehicles: int = 0
        self.race_started_wall: float = time.time()
        self.narrative: list[str] = []     # 발화한 멘트/주요 사건 로그 (LLM 서사 연속성용)
        self.stint_start_lap: int = 0      # 마지막 피트 이후 첫 랩
        self._last_total_laps: Optional[int] = None
        self._pit_seen_this_lap = False
        self._last_fuel: Optional[float] = None
        self._last_session_et: float = -1.0

    # ------------------------------------------------------------------

    @property
    def is_race(self) -> bool:
        return self.session_type is not None and self.session_type >= RACE_SESSION_MIN

    def recent_laps(self, n: int = 3, valid_only: bool = True) -> list[LapRecord]:
        laps = [l for l in self.laps if l.valid] if valid_only else self.laps
        return laps[-n:]

    def baseline_lap_time(self, n: int = 5) -> Optional[float]:
        """최근 유효 랩들의 중앙값 — '평소 페이스'."""
        times = sorted(l.lap_time for l in self.recent_laps(n) if l.lap_time > 0)
        if not times:
            return None
        return times[len(times) // 2]

    def add_narrative(self, text: str) -> None:
        self.narrative.append(text)
        if len(self.narrative) > 60:
            self.narrative = self.narrative[-60:]

    # ------------------------------------------------------------------

    def update(self, snap: Snapshot) -> Optional[LapRecord]:
        """
        매 폴링마다 호출. 랩이 완료된 순간이면 LapRecord를 만들어 반환하고
        아니면 None. 세션 전환도 여기서 감지해 자동 reset한다.
        """
        me = snap.player_scoring()
        if me is None:
            return None
        ses = snap.session

        # 세션 전환 감지: 세션 타입/트랙 변경, 또는 ET가 크게 뒤로 감
        if (self.session_type is not None
                and (ses["session_type"] != self.session_type
                     or ses["track"] != self.track
                     or ses["current_et"] < self._last_session_et - 30)):
            log.info("세션 전환 감지 → 상태 초기화")
            self.reset()

        if self.session_type is None:
            self.session_type = ses["session_type"]
            self.track = ses["track"]
            self.track_len = ses["track_len"]
            self.driver_name = me["driver"]
            self.player_class = me["cls"]
            self.total_vehicles = ses["num_vehicles"]
            self.class_vehicles = sum(
                1 for v in snap.vehicles if v["cls"] == self.player_class)
            self._last_total_laps = me["total_laps"]
            log.info("세션 시작: %s / %s (%s, 동클래스 %d대)",
                     self.track, "레이스" if self.is_race else f"세션{self.session_type}",
                     self.player_class, self.class_vehicles)

        self._last_session_et = ses["current_et"]

        # 피트 통과 추적 (이번 랩에 피트에 있었는지)
        if me["in_pits"] or snap.player.get("in_pitlane"):
            self._pit_seen_this_lap = True

        completed = None
        if self._last_total_laps is not None and me["total_laps"] > self._last_total_laps:
            completed = self._on_lap_complete(snap, me)
        self._last_total_laps = me["total_laps"]
        return completed

    def _on_lap_complete(self, snap: Snapshot, me: dict) -> Optional[LapRecord]:
        lap_time = me["last_lap"]
        fuel_now = snap.player.get("fuel")

        fuel_used = -1.0     # -1 = 알 수 없음(첫 랩) 또는 급유 랩 → 평균 계산에서 제외
        if fuel_now is not None and self._last_fuel is not None:
            delta = self._last_fuel - fuel_now
            if delta >= 0:
                fuel_used = round(delta, 3)
        self._last_fuel = fuel_now

        gap_behind = -1.0
        for v in snap.vehicles:
            if v["place"] == me["place"] + 1 and v["cls"] == me["cls"]:
                gap_behind = v["time_behind_next"]
                break

        wheels = snap.player.get("wheels") or []
        pit_lap = self._pit_seen_this_lap
        self._pit_seen_this_lap = False
        if pit_lap:
            self.stint_start_lap = me["total_laps"] + 1

        rec = LapRecord(
            lap_number=me["total_laps"],
            lap_time=lap_time,
            s1=round(me["last_s1"], 3),
            s2=round(me["last_s2"] - me["last_s1"], 3) if me["last_s2"] > 0 else 0.0,
            s3=round(lap_time - me["last_s2"], 3) if me["last_s2"] > 0 else 0.0,
            place=me["place"],
            fuel_left=round(fuel_now, 2) if fuel_now is not None else -1.0,
            fuel_used=fuel_used,
            gap_ahead=me["time_behind_next"] if me["place"] > 1 else -1.0,
            gap_behind=gap_behind,
            tyre_wear=[w["wear"] for w in wheels],
            tyre_temps=[w["carcass_temp"] for w in wheels],
            brake_temps=[w["brake_temp"] for w in wheels],
            in_pits=pit_lap,
            track_temp=snap.session["track_temp"],
            raining=snap.session["raining"],
            valid=(lap_time > 0 and not pit_lap),
        )
        self.laps.append(rec)
        log.info("랩 %d 완료: %.3fs (연료 %.1fL, 사용 %s)",
                 rec.lap_number, rec.lap_time, rec.fuel_left,
                 f"{rec.fuel_used:.2f}L" if rec.fuel_used >= 0 else "-")
        return rec

    # ------------------------------------------------------------------

    def save_json(self, data_dir: str) -> Optional[str]:
        """레이스 히스토리를 JSON으로 저장하고 경로를 반환."""
        if not self.laps:
            return None
        os.makedirs(data_dir, exist_ok=True)
        stamp = time.strftime("%Y%m%d_%H%M%S")
        path = os.path.join(data_dir, f"race_{stamp}.json")
        payload = {
            "track": self.track,
            "session_type": self.session_type,
            "player_class": self.player_class,
            "driver": self.driver_name,
            "class_vehicles": self.class_vehicles,
            "total_vehicles": self.total_vehicles,
            "laps": [asdict(l) for l in self.laps],
            "narrative": self.narrative,
        }
        with open(path, "w", encoding="utf-8") as f:
            json.dump(payload, f, ensure_ascii=False, indent=2)
        log.info("레이스 히스토리 저장: %s (%d랩)", path, len(self.laps))
        return path
