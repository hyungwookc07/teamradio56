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

        /// <summary>
        /// 바이트 배열 LMU_Data. Windows 이름 있는 매핑처럼: GameAlive=false(게임 종료)여도 우리가
        /// 열어 둔 동안은 마지막 내용이 그대로 읽히고(핸들이 매핑을 살려 둠), 닫은 뒤에야 못 연다.
        /// Absent는 "처음부터 없음"(Enable Plugins 꺼짐 등) — 열려 있어도 바로 못 연다.
        /// </summary>
        private sealed class BlobMemory : ILmuMemory
        {
            public byte[] Data;
            public bool Absent;
            public bool GameAlive = true;
            public long? CapacityOverride;
            public bool FailReads;
            public Action<long, int> OnRead;
            public int Reads;
            public int Closes;
            private bool _open;

            public BlobMemory(byte[] data)
            {
                Data = data;
            }

            public bool TryOpen()
            {
                if (Absent || (!_open && !GameAlive))
                {
                    _open = false;
                    return false;
                }
                _open = true;
                return true;
            }

            public bool IsOpen { get { return _open; } }

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
                Closes++;
                _open = false;
            }

            public void Dispose()
            {
                _open = false;
            }
        }

        /// <summary>rF2 원천 가짜 — Conn이 곧 연결 (버전 규칙 없음).</summary>
        private class FakeSource : ISnapshotSource
        {
            public readonly Snapshot Snap = new Snapshot
            {
                InSession = true,
                Session = new SessionInfo { Track = "rf2-fake" },
                Vehicles = new List<VehicleInfo>(),
            };
            public bool Conn = true;
            public int Polls;

            public virtual Snapshot Poll()
            {
                Polls++;
                return Conn ? Snap : null;
            }

            public virtual bool Connected
            {
                get { return Conn; }
            }

            public void Dispose() { }
        }

        /// <summary>
        /// 실제 rF2 리더와 같은 연결 규칙(Core의 VersionLiveness)으로 도는 가짜 — Conn은 매핑이
        /// 있는지, Version은 스코어링 버전(Moving이면 Fixture.Tick마다 +1). LiveExtended는
        /// Rf2SharedMemoryReader.TryReadLiveExtended와 같이 "버전이 움직일 때만" Extended를 준다.
        /// </summary>
        private sealed class VersionedSource : FakeSource, IVersionTracker
        {
            public readonly VersionLiveness Live = new VersionLiveness();
            public Func<double> Clock;
            public int Version = 1000;
            public bool Moving = true;
            public int Tracks;

            public override Snapshot Poll()
            {
                Polls++;
                Observe();
                return Live.Connected ? Snap : null;
            }

            public override bool Connected
            {
                get { return Live.Connected; }
            }

            public void TrackVersion()
            {
                Tracks++;
                Observe();
            }

            public long VersionChanges
            {
                get { return Live.Changes; }
            }

            private void Observe()
            {
                if (Conn)
                    Live.Observe(Version, Clock());
                else
                    Live.Missing(Clock());
            }

            public ExtendedReader LiveExtended(rF2Extended ext)
            {
                return (out rF2Extended e) =>
                {
                    e = default(rF2Extended);
                    TrackVersion();
                    if (!Live.Connected)
                        return false;
                    e = ext;
                    return ext.mVersionUpdateBegin == ext.mVersionUpdateEnd;
                };
            }
        }

        private sealed class Fixture
        {
            public double Now = 100.0;
            public readonly BlobMemory Mem;
            public readonly FakeSource Rf2;
            public readonly LmuNativeReader Native;
            public readonly TelemetrySource Source;
            public readonly List<string> Info = new List<string>();
            public readonly List<string> Warn = new List<string>();

            /// <param name="versioned">rF2를 실제 버전 규칙으로 (VersionedSource).</param>
            /// <param name="liveExt">versioned일 때 rF2 버전이 움직일 때만 주는 Extended.</param>
            public Fixture(byte[] blob, ExtendedReader ext = null, bool versioned = false, rF2Extended? liveExt = null)
            {
                Mem = new BlobMemory((byte[])blob.Clone());
                if (versioned)
                {
                    var v = new VersionedSource { Clock = () => Now };
                    Rf2 = v;
                    if (liveExt.HasValue)
                        ext = v.LiveExtended(liveExt.Value);
                }
                else
                {
                    Rf2 = new FakeSource();
                }
                Native = new LmuNativeReader(Mem, () => Now, ext);
                Source = new TelemetrySource(Native, Rf2, Info.Add, Warn.Add);
            }

            public VersionedSource V { get { return (VersionedSource)Rf2; } }

            public string Keys()
            {
                bool warn;
                string[] keys = Source.StatusLineKeys(out warn);
                return string.Join("+", keys) + (warn ? " (warn)" : "");
            }

            /// <summary>
            /// LMU 종료: 게임은 꺼졌지만 우리가 쥔 핸들이 매핑을 살려 둔다 (내용 그대로, 읽기 성공).
            /// 리더가 멈춤(5초) 뒤 닫았다 다시 열려다 실패해야 Absent가 된다.
            /// </summary>
            public void ExitGame()
            {
                Mem.GameAlive = false;
                Wait(4.0);
                Wait(2.0);
            }

            /// <summary>LMU 다시 실행 — 새 세션처럼 ET를 되돌리고 0.2초 진행.</summary>
            public Snapshot StartGame()
            {
                Mem.GameAlive = true;
                F64(LmuNativeLayout.CurrentEt, 10.0);
                return Tick();
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
                var v = Rf2 as VersionedSource;
                if (v != null && v.Moving)
                    v.Version++;
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
            check(f.Keys() == "src_waiting", "매핑·DLL 없음 → src_waiting (실제 " + f.Keys() + ")");

            // 4) gameVersion 0 → 쓰지 않음
            f = new Fixture(blob);
            f.I32(LmuNativeLayout.GameVersion, 0);
            s = f.Tick();
            check(f.Native.State == LmuNativeState.NotRunning && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "gameVersion 0 → rF2 (실제 " + f.Native.State + "/" + f.Source.Active + ")");
            f.Rf2.Conn = false;
            s = f.Tick();
            check(s == null && !f.Source.Connected, "gameVersion 0 + DLL 없음 → 대기");
            check(f.Keys() == "src_native_idle", "gameVersion 0 → 매핑은 있음 → src_native_idle (실제 " + f.Keys() + ")");
            // gameVersion 0이 5초 넘게 이어지면 닫았다 다시 연다 — 게임이 꺼졌으면 Absent
            f.Mem.GameAlive = false;
            f.Wait(3.0);
            check(f.Native.State == LmuNativeState.NotRunning && f.Native.Recycles == 0, "NotRunning 3초 → 그대로");
            f.Wait(3.0);
            check(f.Native.State == LmuNativeState.Absent && f.Native.Recycles == 1 && f.Keys() == "src_waiting",
                  "NotRunning 5초 넘음 + 게임 종료 → 닫고 Absent → src_waiting (실제 " + f.Native.State + "/" + f.Keys() + ")");

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
            check(f.Keys() == "src_rf2+src_native_rejected (warn)", "크기 부족 + DLL → rF2 + 거부 경고 (실제 " + f.Keys() + ")");
            f.Rf2.Conn = false;
            f.Tick();
            check(f.Keys() == "src_native_rejected+src_native_rejected_nodll (warn)",
                  "크기 부족 + DLL 없음 → 거부 + DLL 설치 안내, Enable Plugins 안내 없음 (실제 " + f.Keys() + ")");
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
            check(f.Info.Exists(l => l.Contains("LMU 내장 공유 메모리 끊김 — 복사 중 계속 바뀜 (찢긴 읽기"))
                  && !f.Info.Exists(l => l.Contains("검증 대기")),
                  "찢김으로 끊김 → 사유 '찢긴 읽기' (검증 대기 아님): " + string.Join(" | ", f.Info));
            f.Rf2.Conn = false;
            f.Tick();
            check(f.Keys() == "src_native_idle", "계속 찢김 + DLL 없음 → 매핑은 있음 → src_native_idle (실제 " + f.Keys() + ")");

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
            check(f.Keys() == "src_native_idle", "멈춤 + DLL 없음 → src_native_idle (실제 " + f.Keys() + ")");
            // 멈춘 매핑은 닫았다 같은 틱에 다시 연다 — 게임이 쥐고 있으면(일시정지) 그대로 Stale
            check(f.Native.Recycles == 1 && f.Native.State == LmuNativeState.Stale && f.Native.OpenCount == 1
                  && f.Mem.IsOpen, "일시정지 → 닫고 다시 열림, 상태 유지 (recycles " + f.Native.Recycles + ")");
            f.Wait(1.0);
            check(f.Native.Recycles == 1, "다시 열기는 2초 간격");
            f.Wait(1.2);
            check(f.Native.Recycles == 2 && f.Native.State == LmuNativeState.Stale, "2초 뒤 또 다시 열기");
            f.Rf2.Conn = true;
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null && f.Native.OpenCount == 1,
                  "다시 움직이면 내장 (세션 상태 그대로)");
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

            // 13) 플레이어 슬롯 없음 — 정상 상태(리타이어 관전·차 빠짐·로딩)에서도 생기므로 형식
            //     실패로 보지 않는다: 플레이어 정보만 비고 내장 유지, 진단 플래그·로그 한 번
            f = new Fixture(blob);
            f.I32(slot + LmuNativeLayout.TelIdOffset, 77);
            s = f.Tick();
            check(s != null && s.Player == null && f.Source.Active == TelemetrySourceKind.LmuNative,
                  "플레이어 슬롯 없음 → Player null, 내장 유지");
            for (int i = 0; i < 3 * LmuNativeReader.MissingSlotFramesToReport; i++)
                s = f.Tick();
            check(!f.Native.GuardFailed && f.Source.Active == TelemetrySourceKind.LmuNative && s != null,
                  "슬롯을 75프레임 못 찾아도 가드 실패 아님, 내장 유지");
            check(f.Native.PlayerSlotMissing && f.Info.FindAll(l => l.Contains("플레이어 텔레메트리 슬롯이 없음")).Count == 1,
                  "슬롯 없음 진단 플래그 + 로그 한 번");
            check(f.Keys() == "src_native", "슬롯 없음은 상태 줄 그대로 (실제 " + f.Keys() + ")");
            f.I32(slot + LmuNativeLayout.TelIdOffset, PlayerId);
            s = f.Tick();
            check(!f.Native.PlayerSlotMissing && f.Native.Verified && s.Player != null,
                  "슬롯이 돌아오면 플래그 풀리고 검증 통과");
            // 슬롯 없음 + 나쁜 값이 섞여도 슬롯 없음 프레임은 세지 않는다
            f = new Fixture(blob);
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 80.0);
            f.Tick();
            f.Tick();
            f.I32(slot + LmuNativeLayout.TelIdOffset, 77);
            for (int i = 0; i < 40; i++)
                f.Tick();
            check(!f.Native.GuardFailed, "나쁜 프레임 2개 뒤 슬롯 없음 40프레임 → 실패 아님");

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
            check(f.Keys() == "src_rf2+src_native_rejected (warn)", "가드 실패 + DLL → rF2 + 거부 경고 (실제 " + f.Keys() + ")");
            f.Rf2.Conn = false;
            f.Tick();
            check(f.Keys() == "src_native_rejected+src_native_rejected_nodll (warn)",
                  "가드 실패 + DLL 없음 → 거부 + DLL 설치 안내만 (src_waiting 없음, 실제 " + f.Keys() + ")");
            f.Rf2.Conn = true;
            // LMU 종료 → (우리 핸들이 매핑을 살려 두지만) 멈춤 5초 뒤 닫고 다시 열기 실패 → Absent
            f.ExitGame();
            check(f.Native.State == LmuNativeState.Absent && !f.Mem.IsOpen && f.Native.GuardFailed,
                  "게임 종료 → 핸들 놓고 Absent (실제 " + f.Native.State + ", open " + f.Mem.IsOpen + ")");
            // 다시 실행하면 매핑을 새로 열어 검증부터 — 값이 정상이면 바로 그 프레임에서 복귀
            s = f.StartGame();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && f.Native.Verified && s != null
                  && !f.Native.GuardFailed && f.Native.OpenCount == 2,
                  "게임 재시작 + 값 정상 → 가드 풀림, 검증 통과 후 내장 (open " + f.Native.OpenCount + ")");
            // 재시작했는데 여전히 이상하면 첫 프레임부터 안 쓴다 (쓰레기 값을 엔진에 넘기지 않음)
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 80.0);
            f.ExitGame();
            s = f.StartGame();
            check(f.Source.Active == TelemetrySourceKind.Rf2Plugin && ReferenceEquals(s, f.Rf2.Snap),
                  "한 번 실패한 뒤 재시작은 검증 통과 전까지 안 씀");
            // 매핑을 다른 프로그램이 쥐고 있어 같은 매핑을 물려받은 재시작(또는 긴 일시정지): 멈췄다
            // 다시 움직이면 실패한 가드를 한 번 더 판정 — 진짜 형식 문제면 다시 실패, 경고는 처음 한 번
            f = new Fixture(blob);
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 80.0);
            for (int i = 0; i < 3; i++)
                f.Tick();
            check(f.Native.GuardFailed, "가드 실패 (같은 매핑 시나리오)");
            f.Wait(6.0);
            check(f.Native.State == LmuNativeState.Stale && f.Native.OpenCount == 1, "멈춤 — 다시 열려도 같은 매핑");
            for (int i = 0; i < 3; i++)
                f.Tick();
            check(f.Native.GuardFailed && f.Source.Active == TelemetrySourceKind.Rf2Plugin && f.Warn.Count == 1
                  && f.Info.Exists(l => l.Contains("값 검증 다시 실패")),
                  "다시 움직임 + 여전히 이상 → 다시 실패, 경고는 한 번 (warn " + f.Warn.Count + ")");
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 350.0);
            f.Wait(6.0);
            s = f.Tick();
            check(!f.Native.GuardFailed && f.Native.Verified && f.Source.Active == TelemetrySourceKind.LmuNative && s != null,
                  "멈췄다 다시 움직임 + 값 정상 → 가드 풀리고 내장 (SimHub 재시작 불필요)");

            // 15) 차고 안에서는 판정 보류 (타이어 값이 비어 있을 수 있음)
            f = new Fixture(blob);
            f.U8(scoring + LmuNativeLayout.ScoringInGarageOffset, 1);
            for (int w = 0; w < 4; w++)
                f.F64(wheel0 + w * 260 + 128, 0.0);
            for (int i = 0; i < 10; i++)
                s = f.Tick();
            check(!f.Native.GuardFailed && !f.Native.Verified && f.Source.Active == TelemetrySourceKind.LmuNative,
                  "차고 안 → 판정 보류, 내장 유지");
            // 모니터 화면(mInRealtime 0)·완주/리타이어(mFinishStatus ≠ 0)도 보류 — 텔레메트리가 비거나 멈춰 있음
            int inRealtime = LmuNativeLayout.ScoringInfoOffset
                             + Marshal.OffsetOf(typeof(rF2ScoringInfo), "mInRealtime").ToInt32();
            int finishStatus = scoring + Marshal.OffsetOf(typeof(rF2VehicleScoring), "mFinishStatus").ToInt32();
            foreach (var defer in new[] { new[] { inRealtime, 0 }, new[] { finishStatus, 3 } })
            {
                f = new Fixture(blob);
                f.U8(defer[0], (byte)defer[1]);
                f.I32(slot + LmuNativeLayout.TelIdOffset, 77);          // 슬롯도 없음
                for (int i = 0; i < 40; i++)
                    s = f.Tick();
                f.I32(slot + LmuNativeLayout.TelIdOffset, PlayerId);
                for (int w = 0; w < 4; w++)
                    f.F64(wheel0 + w * 260 + 128, 0.0);                  // 타이어 값 비어 있음
                for (int i = 0; i < 10; i++)
                    s = f.Tick();
                check(!f.Native.GuardFailed && !f.Native.Verified && !f.Native.PlayerSlotMissing
                      && f.Source.Active == TelemetrySourceKind.LmuNative,
                      (defer[0] == inRealtime ? "mInRealtime 0" : "mFinishStatus 3") + " → 판정 보류, 내장 유지");
            }

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
            f.Wait(6.0);                 // 일시정지 → rF2(가짜는 항상 연결)로 잠깐 → 재개
            for (int i = 0; i < 2 * TelemetrySource.CrossCheckAfterPolls; i++)
                f.Tick();
            int pollsAfterPause = f.Rf2.Polls;
            check(f.Source.Active == TelemetrySourceKind.LmuNative
                  && f.Warn.FindAll(l => l.Contains("교차 확인")).Count == 1,
                  "일시정지·원천 전환 뒤에도 교차 확인은 매핑마다 한 번 (polls " + pollsAfterPause + ")");
            f.ExitGame();
            f.StartGame();
            for (int i = 0; i < 2 * TelemetrySource.CrossCheckAfterPolls; i++)
                f.Tick();
            check(f.Warn.FindAll(l => l.Contains("교차 확인")).Count == 2, "게임 재시작(매핑 새로 염) → 교차 확인 다시");
            check(SnapshotCompare.Diff(first, first).Count == 0, "SnapshotCompare 자기 자신과 차이 0");

            // 19) 읽는 중 매핑이 사라짐 → Absent, rF2
            f = new Fixture(blob);
            f.Tick();
            f.Mem.FailReads = true;
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Absent && f.Source.Active == TelemetrySourceKind.Rf2Plugin,
                  "읽는 중 매핑 사라짐 → rF2");

            // 20) LMU 종료 — 우리 핸들이 매핑을 살려 둬서 읽기는 계속 성공한다(마지막 프레임 그대로).
            //     멈춤 5초 뒤 닫았다 다시 열기가 실패해야 정말 "없음": idle 안내 대신 src_waiting
            f = new Fixture(blob);
            f.Rf2.Conn = false;
            for (int i = 0; i < 3; i++)
                f.Tick();
            f.Mem.GameAlive = false;
            s = f.Wait(4.0);
            check(f.Source.Active == TelemetrySourceKind.LmuNative && f.Mem.IsOpen, "종료 직후 4초: 아직 내장 (멈춤 판정 전)");
            s = f.Wait(2.0);
            check(f.Native.State == LmuNativeState.Absent && !f.Mem.IsOpen && f.Native.Recycles == 1
                  && !f.Source.NativeIdle && !f.Source.NativeMappingPresent && f.Keys() == "src_waiting",
                  "종료 + 멈춤 → 핸들 놓고 Absent → src_waiting (실제 " + f.Native.State + "/" + f.Keys() + ")");
            check(s == null && !f.Source.Connected, "종료 → 연결 없음");
            int closes = f.Mem.Closes;
            for (int i = 0; i < 10; i++)
                f.Wait(1.0);
            check(f.Native.State == LmuNativeState.Absent && f.Mem.Closes == closes,
                  "꺼진 동안은 없음 그대로 (닫을 핸들 없음)");
            s = f.StartGame();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null && f.Native.OpenCount == 2,
                  "다시 실행 → 새로 열고 내장 (open " + f.Native.OpenCount + ")");

            // 21) VersionLiveness (rF2 경로 연결 규칙) — 처음 본 버전·오래 못 보다 다시 본 버전은 기준값
            var live = new VersionLiveness();
            live.Observe(500, 10.0);
            check(!live.Connected && live.Changes == 0, "처음 본 버전은 기준값 (연결 아님)");
            live.Observe(500, 10.2);
            check(!live.Connected, "같은 버전 → 연결 아님");
            live.Observe(501, 10.4);
            check(live.Connected && live.Changes == 1, "바뀜 → 연결");
            live.Observe(501, 15.3);
            check(live.Connected, "5초 안 → 유지");
            live.Observe(501, 15.5);
            check(!live.Connected, "5초 넘게 그대로 → 끊김");
            live.Missing(15.6);
            live.Observe(900, 40.0);
            check(!live.Connected && live.Changes == 1, "25초 못 보다 다른 버전 → 기준값 (멈춘 버퍼를 새것으로 안 봄)");
            live.Observe(901, 40.2);
            check(live.Connected, "그다음 바뀜 → 연결");

            // 22) 둘 다 있을 때 일시정지 — rF2는 내장 사용 중에도 버전을 따라가므로, 멈춘 rF2가
            //     "방금 갱신됨"으로 넘어오지 않는다 (예전: rF2로 바뀌고 5초 더 연결)
            f = new Fixture(blob, null, true);
            for (int i = 0; i < 5; i++)
                s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && f.V.Tracks >= 5 && f.V.Live.Connected,
                  "내장 사용 중 rF2 버전 추적 (tracks " + f.V.Tracks + ")");
            int infoBefore = f.Info.Count;
            s = f.Wait(4.0);
            check(f.Source.Active == TelemetrySourceKind.LmuNative, "멈춤 4초 → 내장 유지");
            s = f.Wait(2.0);
            check(s == null && f.Source.Active == TelemetrySourceKind.None && !f.Source.Connected,
                  "멈춤 6초 → 멈춘 rF2로 넘어가지 않고 대기 (실제 " + f.Source.Active + ")");
            check(!f.Info.GetRange(infoBefore, f.Info.Count - infoBefore)
                       .Exists(l => l.Contains("rF2 공유 메모리 플러그인 (DLL)")),
                  "일시정지에 rF2 원천 로그 없음");
            for (int i = 0; i < 10; i++)
                s = f.Wait(1.0);
            check(f.Source.Active == TelemetrySourceKind.None, "계속 멈춤 → 대기 유지");
            s = f.Tick();
            check(f.Source.Active == TelemetrySourceKind.LmuNative && s != null, "재개 → 내장");
            // 경계: rF2가 내장보다 한 틱 늦게 마지막으로 바뀌어도, 내려놓은 뒤 다시 바뀌어야 넘어간다
            f.Wait(4.9);
            f.V.Version++;
            s = f.Wait(0.2);
            check(f.Source.Active == TelemetrySourceKind.None,
                  "rF2 마지막 갱신이 조금 늦어도 멈춤이면 넘어가지 않음 (실제 " + f.Source.Active + ")");
            // 내장만 멈추고 rF2는 움직이는 경우(내장 매핑만 갱신 중단)는 rF2가 움직이면 넘어간다
            f.V.Version++;
            s = f.Wait(0.2);
            check(f.Source.Active == TelemetrySourceKind.Rf2Plugin && ReferenceEquals(s, f.Rf2.Snap),
                  "내려놓은 뒤 rF2가 실제로 움직이면 rF2 (실제 " + f.Source.Active + ")");
            // 값 검증 실패(게임은 돔)는 rF2 자체 판정대로 바로 넘어간다
            f = new Fixture(blob, null, true);
            for (int w = 0; w < 4; w++)
                for (int j = 0; j < 3; j++)
                    f.F64(wheel0 + w * 260 + 128 + j * 8, 80.0);
            for (int i = 0; i < 3; i++)
                s = f.Tick();
            check(f.Native.GuardFailed && f.Source.Active == TelemetrySourceKind.Rf2Plugin && ReferenceEquals(s, f.Rf2.Snap),
                  "가드 실패 + 살아 있는 rF2 → 같은 틱에 rF2 (실제 " + f.Source.Active + ")");
            // 처음부터 멈춘 rF2(지난 실행에서 남은 매핑)는 연결로 보지 않는다
            f = new Fixture(blob, null, true);
            f.V.Moving = false;
            f.Mem.Absent = true;
            for (int i = 0; i < 10; i++)
                s = f.Tick();
            check(s == null && f.Source.Active == TelemetrySourceKind.None, "멈춘 rF2만 있음 → 대기");

            // 23) 보충은 DLL이 이번 실행에서 살아 있을 때만 — 버전이 멈춘 Extended(지난 실행의 매핑,
            //     종료 때 0으로 지운 버퍼)는 일관돼 보여도 쓰지 않는다
            if (ext != null)
            {
                rF2Extended extValue;
                ext(out extValue);
                f = new Fixture(blob, null, true, extValue);
                f.V.Moving = false;
                for (int i = 0; i < 5; i++)
                    s = f.Tick();
                check(!f.Source.GapFill && !f.Native.HasExtended && s != null && s.Session.PitSpeedLimitKmh == 0
                      && s.Session.StatusMessage == "" && f.Keys() == "src_native",
                      "멈춘 Extended → 보충 안 함, 0 / 빈 문자열, src_native (실제 " + f.Keys() + ")");
                f.V.Moving = true;
                f.Tick();
                s = f.Tick();
                check(f.Source.GapFill && s != null && Math.Abs(s.Session.PitSpeedLimitKmh - 81.0) < 1e-6
                      && f.Keys() == "src_native_gapfill",
                      "DLL 버전이 움직이면 보충 (실제 " + f.Keys() + ")");
                f.V.Moving = false;
                for (int i = 0; i < 30; i++)
                    s = f.Tick();
                check(!f.Source.GapFill && s.Session.PitSpeedLimitKmh == 0, "DLL이 멈추면 5초 뒤 보충 중단");
            }

            // 24) 프레임이 계속 구조 검사에 걸림(형식 변화) — 처음엔 idle, 25프레임 뒤 거부 경고
            f = new Fixture(blob);
            f.Rf2.Conn = false;
            f.I32(LmuNativeLayout.NumVehicles, 300);
            s = f.Tick();
            check(f.Native.State == LmuNativeState.Invalid && f.Keys() == "src_native_idle",
                  "구조 이상 1프레임 → src_native_idle (Enable Plugins 안내 아님, 실제 " + f.Keys() + ")");
            for (int i = 0; i < LmuNativeReader.InvalidFramesToReject; i++)
                f.Tick();
            check(f.Native.StructurallyRejected && f.Source.NativeRejected
                  && f.Keys() == "src_native_rejected+src_native_rejected_nodll (warn)",
                  "구조 이상 25프레임 → 거부 경고 (실제 " + f.Keys() + ")");
            check(f.Warn.Exists(l => l.Contains("구조 검사에 걸림")), "구조 거부 경고 로그");
            f.I32(LmuNativeLayout.NumVehicles, 5);
            s = f.Tick();
            check(!f.Native.StructurallyRejected && f.Source.Active == TelemetrySourceKind.LmuNative && s != null,
                  "정상 프레임 하나면 풀림");
            check(f.Keys() == "src_native", "정상 → src_native");

            foreach (string failure in failures)
                Console.WriteLine("❌ " + failure);
            if (failures.Count > 0)
            {
                Console.WriteLine("❌ LMU 내장 메모리 셀프테스트 실패 " + failures.Count + "건 (통과 " + pass + ")");
                return 1;
            }
            Console.WriteLine("✅ LMU 내장 메모리 원천 선택·대체 규칙 " + pass
                              + "건 통과 (매핑 없음·gameVersion 0·차량 수·크기·찢김·멈춤·게임 종료/재시작·0대·플레이어 슬롯·값 검증·보충·상태 줄)");
            return 0;
        }
    }
}
