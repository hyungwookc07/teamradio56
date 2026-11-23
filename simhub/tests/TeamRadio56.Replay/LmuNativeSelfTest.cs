using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// LMU 내장 공유 메모리(LMU_Data) 리더 검사 — 게임 없이.
    ///
    ///   --lmu-native-dump native.bin [--extended ext.bin] --out f
    ///       tools/lmu_native_blob.py pack이 명세(docs/lmu_native_layout.json)로 만든 합성 덤프를
    ///       실제 리더(LmuNativeReader)로 읽어 스냅샷 필드를 "키\t값"으로 쓴다. 파이썬이 같은 명세로
    ///       계산한 기대값과 대조 → C# 오프셋·타입 실수를 C# 코드와 독립적으로 잡는다.
    ///   --rf2-dump scoring.bin telemetry.bin [--extended ext.bin] --out f
    ///       같은 레이스 상태를 rF2 플러그인 버퍼로 읽은 스냅샷 (동등성: 두 덤프가 같아야 한다).
    ///   --lmu-native-selftest native.bin [--extended ext.bin]
    ///       원천 선택·대체 규칙: 매핑 없음/게임 버전 0/차량 수 범위 밖/크기 부족/찢긴 읽기/
    ///       멈춤/차량 0대/플레이어 슬롯/값 검증 실패/보충/DLL만 있는 경우.
    /// </summary>
    internal static class LmuNativeSelfTest
    {
        // -- 덤프 -------------------------------------------------------------------

        public static int DumpNative(string blobPath, string extendedPath, string outPath)
        {
            var mem = new BlobMemory(File.ReadAllBytes(blobPath));
            ExtendedReader ext = ExtendedFrom(extendedPath);
            var reader = new LmuNativeReader(mem, () => 0.0, ext);
            Snapshot snap = reader.Poll();
            if (snap == null)
            {
                Console.Error.WriteLine("LMU 내장 메모리 덤프: 스냅샷 없음 (상태 " + reader.State + ", "
                                        + reader.InvalidReason + reader.GuardError + ")");
                return 1;
            }
            Write(outPath, Dump(snap));
            return 0;
        }

        public static int DumpRf2(string scoringPath, string telemetryPath, string extendedPath, string outPath)
        {
            rF2Scoring scoring = FromBytes<rF2Scoring>(File.ReadAllBytes(scoringPath), RF2Sizes.rF2Scoring);
            rF2Telemetry telemetry = FromBytes<rF2Telemetry>(File.ReadAllBytes(telemetryPath), RF2Sizes.rF2Telemetry);
            rF2Extended ext = default(rF2Extended);
            bool hasExt = extendedPath != null;
            if (hasExt)
                ext = FromBytes<rF2Extended>(File.ReadAllBytes(extendedPath), RF2Sizes.rF2Extended);
            Write(outPath, Dump(SnapshotBuilder.Build(scoring, telemetry, hasExt, ext)));
            return 0;
        }

        private static ExtendedReader ExtendedFrom(string path)
        {
            if (path == null)
                return null;
            rF2Extended ext = FromBytes<rF2Extended>(File.ReadAllBytes(path), RF2Sizes.rF2Extended);
            return (out rF2Extended e) =>
            {
                e = ext;
                return ext.mVersionUpdateBegin == ext.mVersionUpdateEnd;
            };
        }

        private static T FromBytes<T>(byte[] bytes, int size) where T : struct
        {
            if (bytes.Length < size)
                throw new InvalidDataException(typeof(T).Name + " 덤프가 짧음: " + bytes.Length + " < " + size);
            GCHandle h = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return (T)Marshal.PtrToStructure(h.AddrOfPinnedObject(), typeof(T));
            }
            finally
            {
                h.Free();
            }
        }

        private static void Write(string outPath, string text)
        {
            if (outPath != null)
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
            else
                Console.Out.Write(text);
        }

        private static string N(double v)
        {
            return v.ToString("F6", CultureInfo.InvariantCulture);
        }

        private static string B(bool v)
        {
            return v ? "1" : "0";
        }

        private static string I(long v)
        {
            return v.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>스냅샷의 분석기용 필드 전부 (T 제외) — 키 이름은 명세의 snapshot 절과 같다.</summary>
        public static string Dump(Snapshot s)
        {
            var sb = new StringBuilder();
            Action<string, string> kv = (k, v) => sb.Append(k).Append('\t').Append(v).Append('\n');
            SessionInfo si = s.Session;
            kv("session.track", si.Track);
            kv("session.session_type", I(si.SessionType));
            kv("session.current_et", N(si.CurrentEt));
            kv("session.end_et", N(si.EndEt));
            kv("session.max_laps", I(si.MaxLaps));
            kv("session.track_length", N(si.TrackLength));
            kv("session.game_phase", I(si.GamePhase));
            kv("session.yellow_state", I(si.YellowState));
            kv("session.sector_flags", si.SectorFlags == null ? "" : string.Join(",", Array.ConvertAll(si.SectorFlags, b => I(b))));
            kv("session.in_realtime", B(si.InRealtime));
            kv("session.raining", N(si.Raining));
            kv("session.dark_cloud", N(si.DarkCloud));
            kv("session.ambient_temp", N(si.AmbientTemp));
            kv("session.track_temp", N(si.TrackTemp));
            kv("session.avg_wetness", N(si.AvgWetness));
            kv("session.num_vehicles", I(si.NumVehicles));
            kv("session.pit_speed_limit_kmh", N(si.PitSpeedLimitKmh));
            kv("session.status_message", si.StatusMessage);
            kv("session.history_message", si.HistoryMessage);
            kv("in_session", B(s.InSession));
            kv("vehicles.count", I(s.Vehicles.Count));
            for (int i = 0; i < s.Vehicles.Count; i++)
            {
                VehicleInfo v = s.Vehicles[i];
                string p = "vehicle." + i + ".";
                kv(p + "id", I(v.Id));
                kv(p + "driver", v.Driver);
                kv(p + "vehicle_name", v.VehicleName);
                kv(p + "class", v.Class);
                kv(p + "is_player", B(v.IsPlayer));
                kv(p + "place", I(v.Place));
                kv(p + "total_laps", I(v.TotalLaps));
                kv(p + "lap_dist", N(v.LapDist));
                kv(p + "path_lateral", N(v.PathLateral));
                kv(p + "sector", I(v.Sector));
                kv(p + "last_lap", N(v.LastLap));
                kv(p + "best_lap", N(v.BestLap));
                kv(p + "last_s1", N(v.LastS1));
                kv(p + "last_s2", N(v.LastS2));
                kv(p + "time_behind_next", N(v.TimeBehindNext));
                kv(p + "laps_behind_next", I(v.LapsBehindNext));
                kv(p + "time_behind_leader", N(v.TimeBehindLeader));
                kv(p + "in_pits", B(v.InPits));
                kv(p + "pit_state", I(v.PitState));
                kv(p + "num_pitstops", I(v.NumPitstops));
                kv(p + "num_penalties", I(v.NumPenalties));
                kv(p + "finish_status", I(v.FinishStatus));
                kv(p + "flag_blue", B(v.FlagBlue));
                kv(p + "estimated_lap", N(v.EstimatedLap));
                kv(p + "time_into_lap", N(v.TimeIntoLap));
                kv(p + "lap_start_et", N(v.LapStartEt));
                kv(p + "in_garage", B(v.InGarage));
                kv(p + "pos", string.Join(",", Array.ConvertAll(v.Pos, N)));
            }
            PlayerInfo pl = s.Player;
            kv("player.present", B(pl != null));
            if (pl == null)
                return sb.ToString();
            kv("player.id", I(pl.Id));
            kv("player.lap_number", I(pl.LapNumber));
            kv("player.speed_kmh", N(pl.SpeedKmh));
            kv("player.rpm", N(pl.Rpm));
            kv("player.max_rpm", N(pl.MaxRpm));
            kv("player.gear", I(pl.Gear));
            kv("player.fuel", N(pl.Fuel));
            kv("player.fuel_capacity", N(pl.FuelCapacity));
            kv("player.water_temp", N(pl.WaterTemp));
            kv("player.oil_temp", N(pl.OilTemp));
            kv("player.overheating", B(pl.Overheating));
            kv("player.detached", B(pl.Detached));
            kv("player.front_wing_height", N(pl.FrontWingHeight));
            kv("player.dent_severity", string.Join(",", Array.ConvertAll(pl.DentSeverity, b => I(b))));
            kv("player.last_impact_et", N(pl.LastImpactEt));
            kv("player.last_impact_mag", N(pl.LastImpactMag));
            kv("player.in_pit_lane", B(pl.InPitLane));
            kv("player.speed_limiter", B(pl.SpeedLimiter));
            kv("player.steering", N(pl.Steering));
            kv("player.yaw_rate", N(pl.YawRate));
            kv("player.lat_vel", N(pl.LatVel));
            kv("player.has_steer_data", B(pl.HasSteerData));
            kv("player.wheels.count", I(pl.Wheels.Length));
            for (int w = 0; w < pl.Wheels.Length; w++)
            {
                WheelInfo wh = pl.Wheels[w];
                string p = "player.wheel." + w + ".";
                kv(p + "brake_temp", N(wh.BrakeTemp));
                kv(p + "pressure", N(wh.Pressure));
                kv(p + "temps", string.Join(",", Array.ConvertAll(wh.Temps, N)));
                kv(p + "carcass_temp", N(wh.CarcassTemp));
                kv(p + "wear", N(wh.Wear));
                kv(p + "flat", B(wh.Flat));
                kv(p + "detached", B(wh.Detached));
            }
            return sb.ToString();
        }

        // -- 대체 규칙 셀프테스트 -----------------------------------------------------

        private sealed class BlobMemory : ILmuMemory
        {
            public byte[] Data;
            public bool Absent;
            public long? CapacityOverride;
            public bool FailReads;
            public Action<long, int> OnRead;
            public int Reads;
            private bool _open;

            public BlobMemory(byte[] data)
            {
                Data = data;
            }

            public bool TryOpen()
            {
                if (Absent)
                {
                    _open = false;
                    return false;
                }
                _open = true;
                return true;
            }

            public long Capacity
            {
                get { return _open ? (CapacityOverride ?? Data.Length) : 0; }
            }

            public bool Read(long offset, byte[] buffer, int index, int count)
            {
                Reads++;
                if (!_open || FailReads || offset + count > Data.Length)
                    return false;
                if (OnRead != null)
                    OnRead(offset, count);
                Buffer.BlockCopy(Data, (int)offset, buffer, index, count);
                return true;
            }

            public void Close()
            {
                _open = false;
            }

            public void Dispose()
            {
                _open = false;
            }
        }

        private sealed class FakeSource : ISnapshotSource
        {
            public readonly Snapshot Snap = new Snapshot
            {
                InSession = true,
                Session = new SessionInfo { Track = "rf2-fake" },
                Vehicles = new List<VehicleInfo>(),
            };
            public bool Conn = true;
            public int Polls;

            public Snapshot Poll()
            {
                Polls++;
                return Conn ? Snap : null;
            }

            public bool Connected
            {
                get { return Conn; }
            }

            public void Dispose() { }
        }

        private sealed class Fixture
        {
            public double Now = 100.0;
            public readonly BlobMemory Mem;
            public readonly FakeSource Rf2 = new FakeSource();
            public readonly LmuNativeReader Native;
            public readonly TelemetrySource Source;
            public readonly List<string> Info = new List<string>();
            public readonly List<string> Warn = new List<string>();

            public Fixture(byte[] blob, ExtendedReader ext = null)
            {
                Mem = new BlobMemory((byte[])blob.Clone());
                Native = new LmuNativeReader(Mem, () => Now, ext);
                Source = new TelemetrySource(Native, Rf2, Info.Add, Warn.Add);
            }

            public void I32(int off, int v) { BitConverter.GetBytes(v).CopyTo(Mem.Data, off); }
            public void F64(int off, double v) { BitConverter.GetBytes(v).CopyTo(Mem.Data, off); }
            public void U8(int off, byte v) { Mem.Data[off] = v; }
            public double Et { get { return BitConverter.ToDouble(Mem.Data, LmuNativeLayout.CurrentEt); } }

            /// <summary>0.2초 흐르고 게임도 0.2초 진행 (mCurrentET 갱신).</summary>
            public Snapshot Tick()
            {
                Now += 0.2;
                F64(LmuNativeLayout.CurrentEt, Et + 0.2);
                return Source.Poll();
            }

            /// <summary>시간만 흐름 (게임 멈춤).</summary>
            public Snapshot Wait(double seconds)
            {
                Now += seconds;
                return Source.Poll();
            }
        }

        // 합성 덤프 시나리오 (tools/lmu_native_blob.py와 같은 값)
        private const int PlayerId = 23;
        private const int PlayerScoringIdx = 2;
        private const int PlayerSlot = 4;
        private const string Track = "Circuit de la Sarthe";

        public static int Run(string blobPath, string extendedPath)
        {
            byte[] blob = File.ReadAllBytes(blobPath);
            ExtendedReader ext = ExtendedFrom(extendedPath);
            var failures = new List<string>();
            int pass = 0;
            Action<bool, string> check = (cond, what) =>
            {
                if (cond)
                    pass++;
                else
                    failures.Add(what);
            };
            int scoring = LmuNativeLayout.VehicleScoring(PlayerScoringIdx);
            int slot = LmuNativeLayout.VehicleTelemetry(PlayerSlot);
            int wheel0 = slot + LmuNativeLayout.TelWheelsOffset;

            check(LmuNativeLayout.Verify() == null, "LmuNativeLayout.Verify: " + LmuNativeLayout.Verify());
            check(blob.Length == LmuNativeLayout.TotalSize, "덤프 크기 " + blob.Length);

            // 1) 둘 다 있으면 내장 메모리 — rF2 버퍼는 읽지도 않는다. 차량 수 불일치(5 vs 6)는 허용
            var f = new Fixture(blob);
            Snapshot s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative, "둘 다 있음 → 내장 (실제 " + f.Source.Active + ")");
            check(s != null && s.Session.Track == Track && s.Vehicles.Count == 5, "내장 스냅샷 (트랙·5대)");
            check(s != null && s.Player != null && s.Player.Id == PlayerId, "플레이어 슬롯 mID 매칭");
            check(f.Rf2.Polls == 0, "내장 사용 중 rF2 미조회");
            check(f.Source.Connected, "연결됨");
            check(f.Mem.Data[LmuNativeLayout.ActiveVehicles] == 6
                  && BitConverter.ToInt32(f.Mem.Data, LmuNativeLayout.NumVehicles) == 5,
                  "시나리오: mNumVehicles 5 ≠ activeVehicles 6");
            check(f.Info.Exists(l => l.Contains("텔레메트리 소스: LMU 내장") && l.Contains("gameVersion=14000")),
                  "원천 로그 (gameVersion 원시값 포함)");
            int logsAfterFirst = f.Info.Count;
            f.Tick();
            f.Tick();
            check(!f.Info.Exists(l => l.Contains("텔레메트리 소스:") && f.Info.IndexOf(l) >= logsAfterFirst),
                  "같은 원천이면 다시 로그하지 않음");
            check(f.Native.Verified, "값 검증 통과 (플레이어 차고 밖)");

            // 2) 매핑 없음 → rF2, 다시 생기면 내장
            f = new Fixture(blob);
            f.Mem.Absent = true;
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.Rf2Plugin && ReferenceEquals(s, f.Rf2.Snap),
                  "매핑 없음 → rF2 스냅샷 그대로 (DLL만 있는 사용자와 같음)");
            check(f.Native.State == LmuNativeState.Absent, "상태 Absent");
            check(f.Info.Exists(l => l.Contains("rF2 공유 메모리 플러그인 (DLL)")), "rF2 원천 로그");
            f.Mem.Absent = false;
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null && s.Session.Track == Track,
                  "매핑이 생기면 내장으로");

            // 3) 둘 다 없음 → 대기
            f = new Fixture(blob);
            f.Mem.Absent = true;
            f.Rf2.Conn = false;
            s = f.Tick();
            check(s == null && !f.Source.Connected && f.Source.Active == TelemetrySourceKind.None, "둘 다 없음 → 대기");
            check(!f.Source.NativeIdle, "매핑 없음은 idle 아님 (설정 화면 'Enable Plugins' 안내)");

            // 4) gameVersion 0 → 쓰지 않음
            f = new Fixture(blob);
            f.I32(LmuNativeLayout.GameVersion, 0);
            s = f.Tick();
            check(f.Native.State == LmuNativeState.NotRunning && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "gameVersion 0 → rF2 (실제 " + f.Native.State + "/" + f.Source.Active + ")");
            f.Rf2.Conn = false;
            s = f.Tick();
            check(s == null && !f.Source.Connected, "gameVersion 0 + DLL 없음 → 대기");

            // 5) 차량 수 범위 밖 → 프레임 버림 (정상 프레임이 한 번도 없으면 연결 안 됨)
            f = new Fixture(blob);
            f.I32(LmuNativeLayout.NumVehicles, 200);
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Invalid && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "mNumVehicles 200 → 버림 → rF2");
            f = new Fixture(blob);
            f.U8(LmuNativeLayout.ActiveVehicles, 200);
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Invalid && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "activeVehicles 200 → 버림 → rF2");
            f = new Fixture(blob);
            f.F64(LmuNativeLayout.CurrentEt, double.NaN);
            s = f.Source.Poll();
            check(f.Native.State == LmuNativeState.Invalid && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "mCurrentET NaN → 버림 → rF2");
            f = new Fixture(blob);
            f.I32(LmuNativeLayout.NumVehicles, -1);
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Invalid, "mNumVehicles -1 → 버림");

            // 6) 매핑이 작음 → 다른 형식
            f = new Fixture(blob);
            f.Mem.CapacityOverride = LmuNativeLayout.TotalSize - 4;
            s = f.Tick();
            check(f.Native.State == LmuNativeState.TooSmall && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "매핑 크기 부족 → rF2");
            f = new Fixture(blob);
            f.Mem.CapacityOverride = LmuNativeLayout.TotalSize + 4;     // C++ 꼬리 패딩
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative, "꼬리 패딩(324824 B)은 허용");

            // 7) 찢긴 읽기 — 한 번이면 재시도로 성공
            f = new Fixture(blob);
            bool tornOnce = true;
            f.Mem.OnRead = (off, n) =>
            {
                if (off == LmuNativeLayout.VehicleScoringOffset && tornOnce)
                {
                    tornOnce = false;
                    f.F64(LmuNativeLayout.CurrentEt, f.Et + 0.01);
                }
            };
            s = f.Tick();
            check(s != null && f.Native.Retries == 1 && f.Native.TornFrames == 0 && f.Native.State == LmuNativeState.Live,
                  "찢김 1번 → 재시도 성공 (retries " + f.Native.Retries + ")");
            check(s != null && Math.Abs(s.Session.CurrentEt - f.Et) < 1e-9, "재시도 프레임은 새 값");

            // 8) 계속 찢김 — 직전 정상 프레임 유지 (null을 내면 엔진이 세션을 초기화한다)
            double goodEt = f.Et;
            f.Mem.OnRead = (off, n) =>
            {
                if (off == LmuNativeLayout.VehicleScoringOffset)
                    f.F64(LmuNativeLayout.CurrentEt, f.Et + 0.01);
            };
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Torn && f.Native.TornFrames == 1, "재시도 4번 실패 → Torn");
            check(s != null && Math.Abs(s.Session.CurrentEt - goodEt) < 1e-9 && s.Player != null,
                  "찢긴 틱은 직전 정상 프레임 값으로");
            check(f.Source.Active == TelemetrySourceKind.LmuNative && f.Rf2.Polls == 0, "찢김으로 원천을 바꾸지 않음");
            for (int i = 0; i < 30; i++)
                s = f.Tick();
            check(ReferenceEquals(s, f.Rf2.Snap) && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "5초 넘게 정상 프레임 없음 → rF2 (실제 " + f.Source.Active + ")");

            // 8b) 텔레메트리 슬롯을 복사하는 사이 mElapsedTime이 바뀌면(100Hz 갱신) 다시 읽는다
            f = new Fixture(blob);
            bool slotTornOnce = true;
            f.Mem.OnRead = (off, n) =>
            {
                if (off == slot && n == LmuNativeLayout.VehicleTelemetryStride && slotTornOnce)
                {
                    slotTornOnce = false;
                    f.Mem.OnRead = null;
                    double el = BitConverter.ToDouble(f.Mem.Data, slot + LmuNativeLayout.TelElapsedOffset);
                    // 복사가 끝난 직후 게임이 쓴 것처럼 — 다음 읽기 전에 바꾼다
                    f.Mem.OnRead = (o2, n2) =>
                    {
                        if (o2 == LmuNativeLayout.EventUpdateScoring)
                        {
                            f.F64(slot + LmuNativeLayout.TelElapsedOffset, el + 0.01);
                            f.Mem.OnRead = null;
                        }
                    };
                }
            };
            s = f.Tick();
            check(s != null && f.Native.Retries == 1 && f.Native.State == LmuNativeState.Live,
                  "슬롯 복사 중 mElapsedTime 변화 → 재시도 (retries " + f.Native.Retries + ")");
            // SME_* 값은 의미 미확정 — 매번 바뀌어도 찢김으로 보지 않는다 (락으로 쓰지 않음)
            f = new Fixture(blob);
            f.Mem.OnRead = (off, n) =>
            {
                if (off == LmuNativeLayout.VehicleScoringOffset)
                    BitConverter.GetBytes(BitConverter.ToUInt32(f.Mem.Data, LmuNativeLayout.EventUpdateTelemetry) + 1)
                        .CopyTo(f.Mem.Data, LmuNativeLayout.EventUpdateTelemetry);
            };
            s = f.Tick();
            check(s != null && f.Native.Retries == 0 && f.Native.State == LmuNativeState.Live,
                  "SME_UPDATE_TELEMETRY가 매번 바뀌어도 정상 프레임");

            // 9) 처음부터 계속 찢김 → 쓸 프레임 없음 → rF2
            f = new Fixture(blob);
            f.Mem.OnRead = (off, n) =>
            {
                if (off == LmuNativeLayout.VehicleScoringOffset)
                    f.F64(LmuNativeLayout.CurrentEt, f.Et + 0.01);
            };
            s = f.Tick();
            check(ReferenceEquals(s, f.Rf2.Snap) && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "정상 프레임 없이 찢김 → rF2");

            // 10) 게임 멈춤 5초 → 끊김(rF2 있으면 rF2), 다시 움직이면 내장
            f = new Fixture(blob);
            f.Tick();
            s = f.Wait(4.0);
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null, "멈춤 4초는 유지 (깜빡임 방지)");
            s = f.Wait(2.0);
            check(f.Native.State == LmuNativeState.Stale && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "멈춤 6초 → rF2 (실제 " + f.Native.State + "/" + f.Source.Active + ")");
            f.Rf2.Conn = false;
            s = f.Wait(0.2);
            check(s == null && !f.Source.Connected, "멈춤 + DLL 없음 → 대기");
            check(f.Source.NativeIdle, "멈춤: 매핑은 있음 (설정 화면 '세션에 들어가면 연결' 안내)");
            f.Rf2.Conn = true;
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null, "다시 움직이면 내장");
            // mCurrentET가 그대로여도 SME_UPDATE_SCORING이 바뀌면 살아 있음
            f.Wait(3.0);
            BitConverter.GetBytes(BitConverter.ToUInt32(f.Mem.Data, LmuNativeLayout.EventUpdateScoring) + 1)
                .CopyTo(f.Mem.Data, LmuNativeLayout.EventUpdateScoring);
            f.Wait(0.2);
            s = f.Wait(3.0);
            check(f.Source.Active == TelemetrySourceKind.LmuNative, "SME_UPDATE_SCORING 변화도 활성으로 인정");

            // 11) 차량 0대 (메뉴/로딩) — 내장 그대로, 세션 아님
            f = new Fixture(blob);
            f.I32(LmuNativeLayout.NumVehicles, 0);
            f.U8(LmuNativeLayout.ActiveVehicles, 0);
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null && !s.InSession
                  && s.Vehicles.Count == 0 && s.Player == null, "차량 0대 → 세션 아님, 플레이어 없음");

            // 12) playerVehicleIdx는 검증된 빠른 길일 뿐 — 틀리거나 꺼져 있어도 mID로 찾는다
            foreach (var hint in new[] { new byte[] { 0, 1 }, new byte[] { PlayerSlot, 0 }, new byte[] { 200, 1 } })
            {
                f = new Fixture(blob);
                f.U8(LmuNativeLayout.PlayerVehicleIdx, hint[0]);
                f.U8(LmuNativeLayout.PlayerHasVehicle, hint[1]);
                s = f.Tick();
                check(s != null && s.Player != null && s.Player.Id == PlayerId,
                      "playerVehicleIdx=" + hint[0] + "/has=" + hint[1] + " → mID로 찾음");
            }

            // 13) 플레이어 슬롯 없음 — 플레이어 정보만 비고, 25프레임 계속되면 형식 이상으로 판정
            f = new Fixture(blob);
            f.I32(slot + LmuNativeLayout.TelIdOffset, 77);
            s = f.Tick();
            check(s != null && s.Player == null && f.Source.Active == TelemetrySourceKind.LmuNative,
                  "플레이어 슬롯 없음 → Player null, 내장 유지");
            for (int i = 0; i < LmuNativeReader.GuardMissingSlotFramesToFail; i++)
                f.Tick();
            check(f.Native.GuardFailed && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "슬롯을 계속 못 찾음 → 가드 실패 → rF2");

            // 14) 값 검증: 타이어 온도가 섭씨처럼 보이면(형식 이동) 3프레임 뒤 실패 → rF2, 크게 로그 1번
            f = new Fixture(blob);
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 80.0);
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && !f.Native.GuardFailed, "가드: 1프레임은 보류");
            f.Tick();
            s = f.Tick();
            check(f.Native.GuardFailed && f.Source.Active == TelemetrySourceKind.Rf2Plugin
                  && ReferenceEquals(s, f.Rf2.Snap), "가드 3프레임 실패 → rF2");
            check(f.Warn.Count == 1 && f.Warn[0].Contains("값 검증 실패") && f.Warn[0].Contains("타이어 온도"),
                  "가드 실패 경고 1번 (사유 포함): " + string.Join(" | ", f.Warn));
            check(f.Source.NativeGuardFailed, "설정 화면용 NativeGuardFailed");
            // 값이 돌아와도 같은 매핑은 계속 안 쓴다 (래치)
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 350.0);
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.Rf2Plugin, "가드 실패는 매핑을 다시 열 때까지 유지");
            check(f.Warn.Count == 1, "경고는 한 번만");
            // 매핑을 다시 열면(게임 재시작) 검증부터 — 값이 정상이면 바로 그 프레임에서 복귀
            f.Mem.Absent = true;
            f.Tick();
            f.Mem.Absent = false;
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && f.Native.Verified && s != null,
                  "재연결 + 값 정상 → 검증 통과 후 내장");
            // 재연결했는데 여전히 이상하면 첫 프레임부터 안 쓴다 (쓰레기 값을 엔진에 넘기지 않음)
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 80.0);
            f.Mem.Absent = true;
            f.Tick();
            f.Mem.Absent = false;
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.Rf2Plugin && ReferenceEquals(s, f.Rf2.Snap),
                  "한 번 실패한 뒤 재연결은 검증 통과 전까지 안 씀");

            // 15) 차고 안에서는 판정 보류 (타이어 값이 비어 있을 수 있음)
            f = new Fixture(blob);
            f.U8(scoring + LmuNativeLayout.ScoringInGarageOffset, 1);
            for (int w = 0; w < 4; w++)
                f.F64(wheel0 + w * 260 + 128, 0.0);
            for (int i = 0; i < 10; i++)
                s = f.Tick();
            check(!f.Native.GuardFailed && !f.Native.Verified && f.Source.Active == TelemetrySourceKind.LmuNative,
                  "차고 안 → 판정 보류, 내장 유지");

            // 16) 보충: Extended가 있으면 세 필드만 거기서, 없으면 0/빈 문자열
            if (ext != null)
            {
                f = new Fixture(blob, ext);
                s = f.Tick();
                check(f.Source.GapFill && s != null && Math.Abs(s.Session.PitSpeedLimitKmh - 81.0) < 1e-6
                      && s.Session.StatusMessage.StartsWith("Drive Through") && s.Session.HistoryMessage.Length > 0,
                      "Extended 보충 (피트 제한속도·메시지)");
                check(f.Info.Exists(l => l.Contains("rF2 Extended에서 보충")), "보충 로그");
            }
            f = new Fixture(blob);
            s = f.Tick();
            check(!f.Source.GapFill && s != null && s.Session.PitSpeedLimitKmh == 0
                  && s.Session.StatusMessage == "" && s.Session.HistoryMessage == "",
                  "Extended 없음 → 0 / 빈 문자열");

            // 17) 내장 경로 끔(레이아웃 자체 검사 실패) → 내장 메모리를 읽지도 않음
            f = new Fixture(blob);
            f.Source.NativeEnabled = false;
            s = f.Tick();
            check(f.Mem.Reads == 0 && f.Source.Active == TelemetrySourceKind.Rf2Plugin, "내장 경로 끔 → rF2만");

            // 18) 진단 교차 확인 — 내장 사용 중 DLL도 있으면 5초쯤 뒤 한 번 rF2와 대조해 로그만 (엔진엔 내장만)
            f = new Fixture(blob);
            Snapshot first = f.Tick();   // 내장 스냅샷 (자기 대조용)
            for (int i = 1; i < TelemetrySource.CrossCheckAfterPolls; i++)
                s = f.Tick();
            check(f.Rf2.Polls == 1 && f.Source.Active == TelemetrySourceKind.LmuNative
                  && s != null && s.Session.Track == Track, "교차 확인: rF2를 한 번만 읽고 내장 스냅샷을 그대로 냄");
            check(f.Warn.Exists(l => l.Contains("교차 확인") && l.Contains("트랙 " + Track + " / rf2-fake")),
                  "교차 확인 차이 로그: " + string.Join(" | ", f.Warn));
            for (int i = 0; i < 40; i++)
                f.Tick();
            check(f.Rf2.Polls == 1, "교차 확인은 세션에 한 번");
            check(SnapshotCompare.Diff(first, first).Count == 0, "SnapshotCompare 자기 자신과 차이 0");

            // 19) 읽는 중 매핑이 사라짐 → Absent, rF2
            f = new Fixture(blob);
            f.Tick();
            f.Mem.FailReads = true;
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Absent && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "읽는 중 매핑 사라짐 → rF2");

            foreach (string failure in failures)
                Console.WriteLine("❌ " + failure);
            if (failures.Count > 0)
            {
                Console.WriteLine("❌ LMU 내장 메모리 셀프테스트 실패 " + failures.Count + "건 (통과 " + pass + ")");
                return 1;
            }
            Console.WriteLine("✅ LMU 내장 메모리 원천 선택·대체 규칙 " + pass
                              + "건 통과 (매핑 없음·gameVersion 0·차량 수·크기·찢김·멈춤·0대·플레이어 슬롯·값 검증·보충)");
            return 0;
        }
    }
}
