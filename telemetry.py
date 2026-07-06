"""
텔레메트리 소스: 공유 메모리(실전) / 리플레이 파일(테스트).

공유 메모리에서 읽은 원시 ctypes 구조체를 순수 파이썬 dict 기반의
Snapshot으로 변환해서 상위 계층(분석기)에 넘긴다. Snapshot은 JSON 직렬화가
가능하므로 --record로 저장한 파일을 --replay로 그대로 재생할 수 있다.
"""

from __future__ import annotations

import json
import logging
import math
import mmap
import sys
import time
from dataclasses import dataclass, field, asdict
from typing import Optional

import ctypes

from rf2data import (
    rF2Telemetry,
    rF2Scoring,
    rF2Extended,
    rFactor2Constants,
    cbytes_to_str,
)

log = logging.getLogger("telemetry")

KELVIN = 273.15


@dataclass
class Snapshot:
    """한 폴링 사이클에 읽은 게임 상태 전체 (순수 파이썬 값만 포함)."""

    t: float = 0.0                    # 수집 시각 (monotonic 기준, 리플레이 시 기록값)
    connected: bool = False           # 공유 메모리가 살아서 갱신되고 있는가
    in_session: bool = False          # 주행 가능한 세션이 진행 중인가
    session: dict = field(default_factory=dict)
    player: dict = field(default_factory=dict)      # 내 차 텔레메트리 (50Hz 버퍼)
    vehicles: list = field(default_factory=list)    # 전 차량 스코어링 (5Hz 버퍼)

    def player_scoring(self) -> Optional[dict]:
        """스코어링 버퍼에서 내 차 항목을 찾는다."""
        for v in self.vehicles:
            if v.get("is_player"):
                return v
        return None

    def to_json(self) -> str:
        return json.dumps(asdict(self), ensure_ascii=False, separators=(",", ":"))

    @classmethod
    def from_json(cls, line: str) -> "Snapshot":
        d = json.loads(line)
        return cls(**d)


def _wheel_to_dict(w) -> dict:
    return {
        "brake_temp": round(w.mBrakeTemp, 1),                       # Celsius
        "pressure": round(w.mPressure, 1),                          # kPa
        "temps": [round(t - KELVIN, 1) for t in w.mTemperature],    # C, left/center/right
        "carcass_temp": round(w.mTireCarcassTemperature - KELVIN, 1),
        "wear": round(w.mWear, 4),
        "flat": bool(w.mFlat),
        "detached": bool(w.mDetached),
    }


def _vehicle_scoring_to_dict(v) -> dict:
    return {
        "id": v.mID,
        "driver": cbytes_to_str(v.mDriverName),
        "vehicle": cbytes_to_str(v.mVehicleName),
        "cls": cbytes_to_str(v.mVehicleClass),
        "is_player": bool(v.mIsPlayer),
        "place": v.mPlace,
        "total_laps": v.mTotalLaps,
        "lap_dist": round(v.mLapDist, 1),
        "path_lat": round(v.mPathLateral, 2),   # 센터 라인 기준 횡방향 위치 (m)
        "sector": v.mSector,
        "last_lap": round(v.mLastLapTime, 3),
        "best_lap": round(v.mBestLapTime, 3),
        "last_s1": round(v.mLastSector1, 3),
        "last_s2": round(v.mLastSector2, 3),        # S1+S2 누적값
        "time_behind_next": round(v.mTimeBehindNext, 3),
        "laps_behind_next": v.mLapsBehindNext,
        "time_behind_leader": round(v.mTimeBehindLeader, 3),
        "in_pits": bool(v.mInPits),
        "pit_state": v.mPitState,
        "num_pitstops": v.mNumPitstops,
        "num_penalties": v.mNumPenalties,
        "finish_status": v.mFinishStatus,
        "flag_blue": v.mFlag == 6,
        "estimated_lap": round(v.mEstimatedLapTime, 3),
        "time_into_lap": round(v.mTimeIntoLap, 3),
        "lap_start_et": round(v.mLapStartET, 3),
        "pos": [round(v.mPos.x, 1), round(v.mPos.y, 1), round(v.mPos.z, 1)],
        "in_garage": bool(v.mInGarageStall),
    }


class TelemetrySource:
    """텔레메트리 소스 공통 인터페이스."""

    def poll(self) -> Optional[Snapshot]:
        """최신 스냅샷을 반환. 아직 데이터가 없으면 None."""
        raise NotImplementedError

    def close(self) -> None:
        pass


class SharedMemoryTelemetry(TelemetrySource):
    """
    rFactor2SharedMemoryMapPlugin64.dll의 공유 메모리를 mmap으로 읽는다.

    주의: Windows에서 mmap(tagname=...)은 매핑이 없으면 새로(0으로 채워진 채)
    만들어버리므로 "열기 성공 = 게임 실행 중"이 아니다. 버전 카운터
    (mVersionUpdateBegin/End)가 실제로 증가하는지로 연결 여부를 판단한다.
    """

    STALE_AFTER_S = 5.0     # 이 시간 동안 버전 카운터가 안 움직이면 연결 끊김으로 간주

    def __init__(self):
        if sys.platform != "win32":
            raise RuntimeError(
                "공유 메모리 모드는 Windows 전용입니다. "
                "다른 OS에서는 --replay 모드를 사용하세요."
            )
        self._tele_mm = None
        self._scor_mm = None
        self._ext_mm = None
        self._last_version = -1
        self._last_version_change = 0.0

    # -- 연결 관리 ---------------------------------------------------------

    def _ensure_mapped(self) -> bool:
        if self._tele_mm is not None:
            return True
        try:
            self._tele_mm = mmap.mmap(
                0, ctypes.sizeof(rF2Telemetry), rFactor2Constants.MM_TELEMETRY_FILE_NAME)
            self._scor_mm = mmap.mmap(
                0, ctypes.sizeof(rF2Scoring), rFactor2Constants.MM_SCORING_FILE_NAME)
            self._ext_mm = mmap.mmap(
                0, ctypes.sizeof(rF2Extended), rFactor2Constants.MM_EXTENDED_FILE_NAME)
            log.info("공유 메모리 매핑 완료")
            return True
        except OSError as e:
            log.debug("공유 메모리 매핑 실패: %s", e)
            self.close()
            return False

    def close(self) -> None:
        for mm_attr in ("_tele_mm", "_scor_mm", "_ext_mm"):
            mm_obj = getattr(self, mm_attr, None)
            if mm_obj is not None:
                try:
                    mm_obj.close()
                except (BufferError, OSError):
                    pass
                setattr(self, mm_attr, None)

    def _read_consistent(self, mm_obj, struct_type):
        """
        버전 블록 검사로 찢어진(torn) 읽기를 방지한다.
        begin != end 이면 게임이 쓰는 도중이므로 재시도.
        """
        for _ in range(3):
            mm_obj.seek(0)
            raw = mm_obj.read(ctypes.sizeof(struct_type))
            data = struct_type.from_buffer_copy(raw)
            if data.mVersionUpdateBegin == data.mVersionUpdateEnd:
                return data
            time.sleep(0.001)
        return data  # 3회 모두 찢겼으면 마지막 것이라도 반환 (다음 폴링에서 회복)

    # -- 폴링 --------------------------------------------------------------

    def poll(self) -> Optional[Snapshot]:
        now = time.monotonic()
        snap = Snapshot(t=now)

        if not self._ensure_mapped():
            return snap  # connected=False

        try:
            scor = self._read_consistent(self._scor_mm, rF2Scoring)
            tele = self._read_consistent(self._tele_mm, rF2Telemetry)
        except (OSError, ValueError) as e:
            log.warning("공유 메모리 읽기 오류, 재연결 예정: %s", e)
            self.close()
            return snap

        # 버전 카운터가 움직이는지로 실제 연결 판단
        version = scor.mVersionUpdateEnd
        if version != self._last_version:
            self._last_version = version
            self._last_version_change = now
        alive = (version > 0) and (now - self._last_version_change < self.STALE_AFTER_S)
        if not alive:
            return snap

        snap.connected = True
        self._fill_snapshot(snap, scor, tele)
        return snap

    def _fill_snapshot(self, snap: Snapshot, scor, tele) -> None:
        info = scor.mScoringInfo
        num = min(info.mNumVehicles, rFactor2Constants.MAX_MAPPED_VEHICLES)
        snap.in_session = num > 0
        snap.session = {
            "track": cbytes_to_str(info.mTrackName),
            "session_type": info.mSession,
            "current_et": round(info.mCurrentET, 3),
            "end_et": round(info.mEndET, 3),
            "max_laps": info.mMaxLaps,
            "track_len": round(info.mLapDist, 1),
            "game_phase": info.mGamePhase,
            "yellow_state": info.mYellowFlagState,
            "in_realtime": bool(info.mInRealtime),
            "raining": round(info.mRaining, 3),
            "dark_cloud": round(info.mDarkCloud, 3),
            "ambient_temp": round(info.mAmbientTemp, 1),
            "track_temp": round(info.mTrackTemp, 1),
            "avg_wetness": round(info.mAvgPathWetness, 3),
            "num_vehicles": num,
        }
        snap.vehicles = [_vehicle_scoring_to_dict(scor.mVehicles[i]) for i in range(num)]

        # 텔레메트리 버퍼에서 내 차 찾기 (스코어링의 player ID와 매칭)
        player_id = None
        for v in snap.vehicles:
            if v["is_player"]:
                player_id = v["id"]
                break
        if player_id is None:
            return

        num_tele = min(tele.mNumVehicles, rFactor2Constants.MAX_MAPPED_VEHICLES)
        for i in range(num_tele):
            vt = tele.mVehicles[i]
            if vt.mID != player_id:
                continue
            vel = vt.mLocalVel
            speed_ms = math.sqrt(vel.x ** 2 + vel.y ** 2 + vel.z ** 2)
            snap.player = {
                "id": vt.mID,
                "lap_number": vt.mLapNumber,
                "lap_start_et": round(vt.mLapStartET, 3),
                "speed_kmh": round(speed_ms * 3.6, 1),
                "rpm": round(vt.mEngineRPM),
                "max_rpm": round(vt.mEngineMaxRPM),
                "gear": vt.mGear,
                "fuel": round(vt.mFuel, 2),
                "fuel_capacity": round(vt.mFuelCapacity, 1),
                "water_temp": round(vt.mEngineWaterTemp, 1),
                "oil_temp": round(vt.mEngineOilTemp, 1),
                "overheating": bool(vt.mOverheating),
                "detached": bool(vt.mDetached),
                "dent_severity": list(vt.mDentSeverity),
                "last_impact_et": round(vt.mLastImpactET, 3),
                "last_impact_mag": round(vt.mLastImpactMagnitude, 1),
                "in_pitlane": vt.mCurrentSector < 0,
                "speed_limiter": bool(vt.mSpeedLimiter),
                "wheels": [_wheel_to_dict(w) for w in vt.mWheels],  # FL FR RL RR
            }
            break


class ReplayTelemetry(TelemetrySource):
    """
    --record로 저장한 JSONL 스냅샷 파일을 재생한다. 게임 없이 전체 파이프라인을
    테스트하기 위한 mock 모드. speed 배속으로 재생 속도 조절 가능.
    """

    def __init__(self, path: str, speed: float = 1.0):
        self._file = open(path, "r", encoding="utf-8")
        self._speed = max(speed, 0.01)
        self._start_wall: Optional[float] = None
        self._start_rec: Optional[float] = None
        self._pending: Optional[Snapshot] = None
        self.finished = False
        log.info("리플레이 모드: %s (배속 x%.1f)", path, speed)

    def poll(self) -> Optional[Snapshot]:
        if self.finished:
            return None
        now = time.monotonic()

        if self._pending is None:
            snap = self._read_next()
            if snap is None:
                return None
            self._pending = snap

        if self._start_wall is None:
            self._start_wall = now
            self._start_rec = self._pending.t

        # 기록 시각 기준으로 아직 재생 시점이 안 됐으면 이전 상태 유지(None 반환 안 함)
        latest = None
        while self._pending is not None:
            due = self._start_wall + (self._pending.t - self._start_rec) / self._speed
            if due > now:
                break
            latest = self._pending
            self._pending = self._read_next()
        if latest is not None:
            latest.t = now  # 이후 로직은 현재 시각 기준으로 동작
        return latest

    def _read_next(self) -> Optional[Snapshot]:
        line = self._file.readline()
        while line:
            line = line.strip()
            if line:
                try:
                    return Snapshot.from_json(line)
                except (json.JSONDecodeError, TypeError) as e:
                    log.warning("리플레이 라인 파싱 실패: %s", e)
            line = self._file.readline()
        self.finished = True
        log.info("리플레이 파일 끝")
        return None

    def close(self) -> None:
        self._file.close()


class SnapshotRecorder:
    """스냅샷을 JSONL로 저장 (--record). 나중에 --replay로 재생."""

    def __init__(self, path: str):
        self._file = open(path, "w", encoding="utf-8")
        log.info("텔레메트리 녹화: %s", path)

    def write(self, snap: Snapshot) -> None:
        self._file.write(snap.to_json() + "\n")

    def close(self) -> None:
        self._file.close()
