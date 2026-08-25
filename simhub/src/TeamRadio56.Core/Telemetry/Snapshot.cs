using System;
using System.Collections.Generic;
using System.Text;

namespace TeamRadio56.Core.Telemetry
{
    /// <summary>휠 하나의 상태 (FL, FR, RL, RR 순).</summary>
    public sealed class WheelInfo
    {
        public double BrakeTemp;        // Celsius
        public double Pressure;         // kPa
        public double[] Temps;          // Celsius, left/center/right
        public double CarcassTemp;      // Celsius
        public double Wear;             // 1.0 = 새 타이어
        public bool Flat;
        public bool Detached;
    }

    /// <summary>내 차 텔레메트리 (50Hz 버퍼에서).</summary>
    public sealed class PlayerInfo
    {
        public int Id;
        public int LapNumber;
        public double SpeedKmh;
        public double Rpm;
        public double MaxRpm;
        public int Gear;
        public double Fuel;
        public double FuelCapacity;
        public double WaterTemp;
        public double OilTemp;
        public bool Overheating;
        public bool Detached;           // 차체 부품 탈락
        public double FrontWingHeight;  // m — 윙 손상 감지용
        public byte[] DentSeverity;     // 8존, 0/1/2
        public double LastImpactEt;
        public double LastImpactMag;
        public bool InPitLane;
        public bool SpeedLimiter;
        public double Steering;         // -1..1, 얼라인 감지용
        public double YawRate;          // rad/s
        public double LatVel;           // m/s 횡속도 — 슬라이드 판정용
        public WheelInfo[] Wheels;      // FL FR RL RR
    }

    /// <summary>전 차량 스코어링 (5Hz 버퍼에서).</summary>
    public sealed class VehicleInfo
    {
        public int Id;
        public string Driver;
        public string VehicleName;
        public string Class;
        public bool IsPlayer;
        public int Place;
        public int TotalLaps;
        public double LapDist;
        public double PathLateral;
        public int Sector;
        public double LastLap;
        public double BestLap;
        public double LastS1;
        public double LastS2;           // S1+S2 누적값
        public double TimeBehindNext;
        public int LapsBehindNext;
        public double TimeBehindLeader;
        public bool InPits;
        public int PitState;
        public int NumPitstops;
        public int NumPenalties;
        public int FinishStatus;
        public bool FlagBlue;
        public double EstimatedLap;
        public double TimeIntoLap;
        public bool InGarage;
        public double[] Pos = new double[0];   // 월드 좌표 (x,y,z) — 실존 필터용
    }

    /// <summary>세션 전역 상태.</summary>
    public sealed class SessionInfo
    {
        public string Track;
        public int SessionType;
        public double CurrentEt;
        public double EndEt;
        public int MaxLaps;
        public double TrackLength;
        public int GamePhase;
        public int YellowState;
        public byte[] SectorFlags;
        public double PitSpeedLimitKmh;   // Extended, 0이면 불명
        public bool InRealtime;
        public double Raining;
        public double DarkCloud;
        public double AmbientTemp;
        public double TrackTemp;
        public double AvgWetness;
        public int NumVehicles;
        public string StatusMessage;      // 게임 메시지 센터 (페널티 사유 등)
        public string HistoryMessage;
    }

    /// <summary>한 틱의 정규화된 상태. 분석기는 이것만 본다.</summary>
    public sealed class Snapshot
    {
        public double T;                  // 수집 시각 (초, monotonic 성격)
        public bool InSession;
        public SessionInfo Session;
        public PlayerInfo Player;
        public List<VehicleInfo> Vehicles;

        public VehicleInfo PlayerScoring()
        {
            if (Vehicles == null)
                return null;
            for (int i = 0; i < Vehicles.Count; i++)
            {
                if (Vehicles[i].IsPlayer)
                    return Vehicles[i];
            }
            return null;
        }
    }

    /// <summary>rF2 원시 구조체 → Snapshot 변환 (telemetry.py 포팅).</summary>
    public static class SnapshotBuilder
    {
        private const double Kelvin = 273.15;
        private const int MaxMappedVehicles = 128;

        private static readonly DateTime Epoch = DateTime.UtcNow;

        public static string ToStr(byte[] raw)
        {
            if (raw == null)
                return string.Empty;
            int len = 0;
            while (len < raw.Length && raw[len] != 0)
                len++;
            return Encoding.GetEncoding("ISO-8859-1").GetString(raw, 0, len).Trim();
        }

        public static Snapshot Build(rF2Scoring scoring, rF2Telemetry telemetry,
                                     bool hasExtended, rF2Extended extended)
        {
            rF2ScoringInfo info = scoring.mScoringInfo;
            int num = Math.Min(info.mNumVehicles, MaxMappedVehicles);

            var snap = new Snapshot
            {
                T = (DateTime.UtcNow - Epoch).TotalSeconds,
                InSession = num > 0,
                Vehicles = new List<VehicleInfo>(Math.Max(num, 0)),
                Session = new SessionInfo
                {
                    Track = ToStr(info.mTrackName),
                    SessionType = info.mSession,
                    CurrentEt = info.mCurrentET,
                    EndEt = info.mEndET,
                    MaxLaps = info.mMaxLaps,
                    TrackLength = info.mLapDist,
                    GamePhase = info.mGamePhase,
                    YellowState = info.mYellowFlagState,
                    SectorFlags = info.mSectorFlag,
                    PitSpeedLimitKmh = hasExtended
                        ? extended.mCurrentPitSpeedLimit * 3.6
                        : 0.0,
                    InRealtime = info.mInRealtime != 0,
                    Raining = info.mRaining,
                    DarkCloud = info.mDarkCloud,
                    AmbientTemp = info.mAmbientTemp,
                    TrackTemp = info.mTrackTemp,
                    AvgWetness = info.mAvgPathWetness,
                    NumVehicles = num,
                    StatusMessage = hasExtended ? ToStr(extended.mStatusMessage) : string.Empty,
                    HistoryMessage = hasExtended ? ToStr(extended.mLastHistoryMessage) : string.Empty,
                },
            };

            int playerId = -1;
            for (int i = 0; i < num; i++)
            {
                VehicleInfo v = FromScoring(scoring.mVehicles[i]);
                snap.Vehicles.Add(v);
                if (v.IsPlayer)
                    playerId = v.Id;
            }

            snap.Player = FindPlayerTelemetry(telemetry, playerId);
            return snap;
        }

        private static VehicleInfo FromScoring(rF2VehicleScoring v)
        {
            return new VehicleInfo
            {
                Id = v.mID,
                Driver = ToStr(v.mDriverName),
                VehicleName = ToStr(v.mVehicleName),
                Class = ToStr(v.mVehicleClass),
                IsPlayer = v.mIsPlayer != 0,
                Place = v.mPlace,
                TotalLaps = v.mTotalLaps,
                LapDist = v.mLapDist,
                PathLateral = v.mPathLateral,
                Sector = v.mSector,
                LastLap = v.mLastLapTime,
                BestLap = v.mBestLapTime,
                LastS1 = v.mLastSector1,
                LastS2 = v.mLastSector2,
                TimeBehindNext = v.mTimeBehindNext,
                LapsBehindNext = v.mLapsBehindNext,
                TimeBehindLeader = v.mTimeBehindLeader,
                InPits = v.mInPits != 0,
                PitState = v.mPitState,
                NumPitstops = v.mNumPitstops,
                NumPenalties = v.mNumPenalties,
                FinishStatus = v.mFinishStatus,
                FlagBlue = v.mFlag == 6,
                EstimatedLap = v.mEstimatedLapTime,
                TimeIntoLap = v.mTimeIntoLap,
                InGarage = v.mInGarageStall != 0,
                Pos = new[] { v.mPos.x, v.mPos.y, v.mPos.z },
            };
        }

        private static PlayerInfo FindPlayerTelemetry(rF2Telemetry telemetry, int playerId)
        {
            if (telemetry.mVehicles == null || playerId < 0)
                return null;

            int count = Math.Min(telemetry.mNumVehicles, MaxMappedVehicles);
            for (int i = 0; i < count; i++)
            {
                rF2VehicleTelemetry vt = telemetry.mVehicles[i];
                if (vt.mID != playerId)
                    continue;

                double speedMs = Math.Sqrt(
                    vt.mLocalVel.x * vt.mLocalVel.x +
                    vt.mLocalVel.y * vt.mLocalVel.y +
                    vt.mLocalVel.z * vt.mLocalVel.z);

                return new PlayerInfo
                {
                    Id = vt.mID,
                    LapNumber = vt.mLapNumber,
                    SpeedKmh = speedMs * 3.6,
                    Rpm = vt.mEngineRPM,
                    MaxRpm = vt.mEngineMaxRPM,
                    Gear = vt.mGear,
                    Fuel = vt.mFuel,
                    FuelCapacity = vt.mFuelCapacity,
                    WaterTemp = vt.mEngineWaterTemp,
                    OilTemp = vt.mEngineOilTemp,
                    Overheating = vt.mOverheating != 0,
                    Detached = vt.mDetached != 0,
                    FrontWingHeight = vt.mFrontWingHeight,
                    DentSeverity = vt.mDentSeverity,
                    LastImpactEt = vt.mLastImpactET,
                    LastImpactMag = vt.mLastImpactMagnitude,
                    InPitLane = vt.mCurrentSector < 0,
                    SpeedLimiter = vt.mSpeedLimiter != 0,
                    Steering = vt.mUnfilteredSteering,
                    YawRate = vt.mLocalRot.y,
                    LatVel = vt.mLocalVel.x,
                    Wheels = BuildWheels(vt.mWheels),
                };
            }
            return null;
        }

        private static WheelInfo[] BuildWheels(rF2Wheel[] raw)
        {
            if (raw == null)
                return new WheelInfo[0];
            var wheels = new WheelInfo[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                rF2Wheel w = raw[i];
                var temps = new double[3];
                if (w.mTemperature != null)
                {
                    for (int j = 0; j < 3 && j < w.mTemperature.Length; j++)
                        temps[j] = w.mTemperature[j] - Kelvin;
                }
                wheels[i] = new WheelInfo
                {
                    BrakeTemp = w.mBrakeTemp,
                    Pressure = w.mPressure,
                    Temps = temps,
                    CarcassTemp = w.mTireCarcassTemperature - Kelvin,
                    Wear = w.mWear,
                    Flat = w.mFlat != 0,
                    Detached = w.mDetached != 0,
                };
            }
            return wheels;
        }
    }
}
