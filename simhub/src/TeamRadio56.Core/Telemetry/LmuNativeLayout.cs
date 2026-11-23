using System;
using System.Runtime.InteropServices;

namespace TeamRadio56.Core.Telemetry
{
    /// <summary>
    /// LMU(v1.2+)가 직접 내는 공유 메모리 "LMU_Data"의 레이아웃 상수.
    ///
    /// 공식 헤더(S397 SharedMemoryInterface)는 손에 없어서, 독립된 공개 구현 둘
    /// (pyLMUSharedMemory lmu_data.py, lmu-pitwall — 둘 다 MIT)이 일치하는 레이아웃 사실만 옮겨
    /// 우리 코드로 다시 썼다 (코드 복사 없음). 기계가 읽는 명세는 docs/lmu_native_layout.json,
    /// 독립 검증은 tools/lmu_native_blob.py (명세로 합성 덤프를 만들어 이 리더와 대조).
    ///
    /// 핵심 사실: 차량별 레코드(ScoringInfo 548 B, VehicleScoring 584 B, VehicleTelemetry
    /// 1888 B, Wheel 260 B)는 rF2 플러그인이 복사해 주는 V01 레코드와 크기·앞부분이 바이트까지
    /// 같다 — LMU는 끝의 mExpansion 바이트에 새 필드를 넣었을 뿐이다. 그래서 기존 rF2 구조체
    /// (RF2Data.Generated.cs)로 그대로 마샬링하고 기존 SnapshotBuilder를 그대로 쓴다.
    ///
    /// #pragma pack(4), 리틀 엔디언. 차량 배열은 104칸(rF2는 128).
    /// </summary>
    public static class LmuNativeLayout
    {
        public const string MappingName = "LMU_Data";

        /// <summary>sizeof(SharedMemoryObjectOut). C++ 꼬리 패딩(324824)이 있어도 되게 "이상"으로 본다.</summary>
        public const int TotalSize = 324820;
        public const int MaxVehicles = 104;

        // -- 최상위 블록 (절대 오프셋) ------------------------------------------------
        public const int GenericOffset = 0;
        public const int GenericSize = 332;          // 이벤트 u32 x16 + gameVersion + FFBTorque + appInfo(260)
        public const int PathsOffset = 332;
        public const int PathsSize = 1300;           // char[260] x5
        public const int ScoringOffset = 1632;
        public const int ScoringSize = 126832;
        public const int TelemetryOffset = 128464;
        public const int TelemetrySize = 196356;

        // -- Generic --------------------------------------------------------------
        public const int EventCount = 16;
        /// <summary>SME_UPDATE_SCORING (u32) — 값의 의미는 미확정, "바뀌었나"만 본다.</summary>
        public const int EventUpdateScoring = 40;
        /// <summary>SME_UPDATE_TELEMETRY (u32).</summary>
        public const int EventUpdateTelemetry = 44;
        /// <summary>gameVersion (i32) — 0보다 크면 게임이 살아 있다. 인코딩은 미확정이라 로그만.</summary>
        public const int GameVersion = 64;
        public const int AppInfoOffset = 72;
        public const int AppInfoSize = 260;

        // -- Scoring 블록 ---------------------------------------------------------
        public const int ScoringInfoOffset = ScoringOffset;                 // 1632, 548 B
        public const int ScoringInfoSize = 548;
        public const int ScoringStreamSizeOffset = ScoringOffset + 548;     // 2180, 12 B (불투명)
        public const int VehicleScoringOffset = ScoringOffset + 560;        // 2192
        public const int VehicleScoringStride = 584;
        public const int ScoringStreamOffset = ScoringOffset + 61296;       // 62928, 64 KiB (형식 미공개, 안 읽음)
        public const int ScoringStreamSize = 65536;

        /// <summary>ScoringInfo.mCurrentET (f64) 절대 오프셋 — 활성 판정·찢김 감시.</summary>
        public const int CurrentEt = ScoringInfoOffset + 68;                // 1700
        /// <summary>ScoringInfo.mNumVehicles (i32) 절대 오프셋.</summary>
        public const int NumVehicles = ScoringInfoOffset + 104;             // 1736

        // -- Telemetry 블록 -------------------------------------------------------
        public const int ActiveVehicles = TelemetryOffset;                  // u8
        public const int PlayerVehicleIdx = TelemetryOffset + 1;            // u8 — telemInfo 인덱스 (빠른 길, 검증 후에만)
        public const int PlayerHasVehicle = TelemetryOffset + 2;            // u8
        public const int VehicleTelemetryOffset = TelemetryOffset + 4;      // 128468
        public const int VehicleTelemetryStride = 1888;

        // -- 레코드 안 오프셋 (rF2 V01과 같음 — Verify가 rF2 구조체와 대조) ---------------
        public const int TelIdOffset = 0;            // VehicleTelemetry.mID (i32)
        public const int TelElapsedOffset = 12;      // VehicleTelemetry.mElapsedTime (f64)
        public const int TelWheelsOffset = 848;      // VehicleTelemetry.mWheels[4]
        public const int ScoringIdOffset = 0;        // VehicleScoring.mID
        public const int ScoringIsPlayerOffset = 196;
        public const int ScoringInGarageOffset = 507;

        public static int VehicleScoring(int i)
        {
            return VehicleScoringOffset + VehicleScoringStride * i;
        }

        public static int VehicleTelemetry(int i)
        {
            return VehicleTelemetryOffset + VehicleTelemetryStride * i;
        }

        /// <summary>
        /// 상수끼리, 그리고 rF2 구조체 레이아웃과 맞는지 시작 시 검사한다 (VerifyLayout과 같은 역할).
        /// 하나라도 어긋나면 내장 메모리 경로를 끄고 rF2 DLL 경로만 쓴다. 정상이면 null.
        /// </summary>
        public static string Verify()
        {
            var problems = new System.Collections.Generic.List<string>();
            Action<string, long, long> eq = (name, actual, expected) =>
            {
                if (actual != expected)
                    problems.Add(string.Format("{0}: {1} (기대 {2})", name, actual, expected));
            };

            // 블록 크기·연쇄
            eq("Generic 크기", EventCount * 4 + 4 + 4 + AppInfoSize, GenericSize);
            eq("AppInfo 크기", 8 + 4 * 4 + 1 + 31 + 204, AppInfoSize);
            eq("Paths 크기", 5 * 260, PathsSize);
            eq("Paths 오프셋", GenericOffset + GenericSize, PathsOffset);
            eq("Scoring 오프셋", PathsOffset + PathsSize, ScoringOffset);
            eq("Scoring 크기", ScoringInfoSize + 12 + MaxVehicles * VehicleScoringStride + ScoringStreamSize,
                ScoringSize);
            eq("scoringStream 오프셋", VehicleScoringOffset + MaxVehicles * VehicleScoringStride,
                ScoringStreamOffset);
            eq("Telemetry 오프셋", ScoringOffset + ScoringSize, TelemetryOffset);
            eq("Telemetry 크기", 4 + MaxVehicles * VehicleTelemetryStride, TelemetrySize);
            eq("전체 크기", TelemetryOffset + TelemetrySize, TotalSize);
            eq("절대 1632", ScoringInfoOffset, 1632);
            eq("절대 2192", VehicleScoringOffset, 2192);
            eq("절대 128464", ActiveVehicles, 128464);
            eq("절대 128468", VehicleTelemetryOffset, 128468);
            eq("마지막 텔레메트리 끝", VehicleTelemetry(MaxVehicles), TotalSize);

            // 레코드는 rF2 V01과 같아야 기존 구조체로 마샬링할 수 있다
            eq("rF2ScoringInfo 크기", Marshal.SizeOf(typeof(rF2ScoringInfo)), ScoringInfoSize);
            eq("rF2VehicleScoring 크기", Marshal.SizeOf(typeof(rF2VehicleScoring)), VehicleScoringStride);
            eq("rF2VehicleTelemetry 크기", Marshal.SizeOf(typeof(rF2VehicleTelemetry)), VehicleTelemetryStride);
            eq("rF2Wheel 크기", Marshal.SizeOf(typeof(rF2Wheel)), 260);
            eq("ScoringInfo.mCurrentET", Off(typeof(rF2ScoringInfo), "mCurrentET"), CurrentEt - ScoringInfoOffset);
            eq("ScoringInfo.mNumVehicles", Off(typeof(rF2ScoringInfo), "mNumVehicles"),
                NumVehicles - ScoringInfoOffset);
            eq("ScoringInfo.mTrackName", Off(typeof(rF2ScoringInfo), "mTrackName"), 0);
            eq("ScoringInfo.mAvgPathWetness", Off(typeof(rF2ScoringInfo), "mAvgPathWetness"), 332);
            eq("VehicleScoring.mID", Off(typeof(rF2VehicleScoring), "mID"), ScoringIdOffset);
            eq("VehicleScoring.mIsPlayer", Off(typeof(rF2VehicleScoring), "mIsPlayer"), ScoringIsPlayerOffset);
            eq("VehicleScoring.mInGarageStall", Off(typeof(rF2VehicleScoring), "mInGarageStall"),
                ScoringInGarageOffset);
            eq("VehicleScoring.mPos", Off(typeof(rF2VehicleScoring), "mPos"), 264);
            eq("VehicleScoring.mFlag", Off(typeof(rF2VehicleScoring), "mFlag"), 504);
            eq("VehicleTelemetry.mID", Off(typeof(rF2VehicleTelemetry), "mID"), TelIdOffset);
            eq("VehicleTelemetry.mElapsedTime", Off(typeof(rF2VehicleTelemetry), "mElapsedTime"), TelElapsedOffset);
            eq("VehicleTelemetry.mLocalVel", Off(typeof(rF2VehicleTelemetry), "mLocalVel"), 184);
            eq("VehicleTelemetry.mFuel", Off(typeof(rF2VehicleTelemetry), "mFuel"), 524);
            eq("VehicleTelemetry.mCurrentSector", Off(typeof(rF2VehicleTelemetry), "mCurrentSector"), 600);
            eq("VehicleTelemetry.mFuelCapacity", Off(typeof(rF2VehicleTelemetry), "mFuelCapacity"), 608);
            eq("VehicleTelemetry.mWheels", Off(typeof(rF2VehicleTelemetry), "mWheels"), TelWheelsOffset);
            eq("Wheel.mPressure", Off(typeof(rF2Wheel), "mPressure"), 120);
            eq("Wheel.mTemperature", Off(typeof(rF2Wheel), "mTemperature"), 128);
            eq("Wheel.mWear", Off(typeof(rF2Wheel), "mWear"), 152);
            eq("Wheel.mTireCarcassTemperature", Off(typeof(rF2Wheel), "mTireCarcassTemperature"), 204);

            return problems.Count == 0 ? null : string.Join("; ", problems.ToArray());
        }

        private static long Off(Type t, string field)
        {
            return Marshal.OffsetOf(t, field).ToInt64();
        }
    }
}
