using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace TeamRadio56.Core.Telemetry
{
    /// <summary>
    /// rF2/LMU 공유 메모리 버퍼 하나를 읽는다.
    ///
    /// 플러그인이 쓰는 도중에 읽으면 반쯤 갱신된 값이 나오므로,
    /// mVersionUpdateBegin == mVersionUpdateEnd 일 때만 채택한다 (정합성 읽기).
    /// </summary>
    public sealed class MappedBuffer<T> : IDisposable where T : struct
    {
        private const int ReadRetries = 4;
        // 매핑이 없을 때(게임 꺼짐·DLL 없음) 틱마다 예외를 내지 않게 — 실패 뒤 1초는 다시 열지 않는다
        private const int RetryMs = 1000;

        private readonly string _name;
        private readonly int _size;
        private readonly byte[] _bytes;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private readonly byte[] _head = new byte[8];
        private bool _failed;
        private int _lastFailTick;

        public MappedBuffer(string name, int size)
        {
            _name = name;
            _size = size;
            _bytes = new byte[size];
        }

        public bool IsOpen { get { return _view != null; } }

        public bool TryOpen()
        {
            if (_view != null)
                return true;
            int now = Environment.TickCount;
            if (_failed && unchecked(now - _lastFailTick) < RetryMs)
                return false;
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(_name, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, _size, MemoryMappedFileAccess.Read);
                _failed = false;
                return true;
            }
            catch (Exception)
            {
                // 게임 미실행/플러그인 비활성 — 정상 상황이므로 조용히 실패
                Close();
                _failed = true;
                _lastFailTick = now;
                return false;
            }
        }

        /// <summary>
        /// 버퍼 맨 앞 두 int(mVersionUpdateBegin/End)만 읽는다 — 스냅샷을 만들지 않고 버전만
        /// 따라갈 때 (Rf2SharedMemoryReader.TrackVersion). 같을 때만 true.
        /// </summary>
        public bool TryReadVersion(out int version)
        {
            version = 0;
            if (!TryOpen())
                return false;
            for (int attempt = 0; attempt < ReadRetries; attempt++)
            {
                try
                {
                    _view.ReadArray(0, _head, 0, 8);
                }
                catch (Exception)
                {
                    Close();
                    return false;
                }
                int begin = BitConverter.ToInt32(_head, 0);
                int end = BitConverter.ToInt32(_head, 4);
                if (begin == end)
                {
                    version = end;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 버전 카운터가 일치하는 스냅샷을 읽는다. 실패 시 false.
        /// </summary>
        public bool TryRead(Func<T, int> versionBegin, Func<T, int> versionEnd, out T value)
        {
            value = default(T);
            if (!TryOpen())
                return false;

            for (int attempt = 0; attempt < ReadRetries; attempt++)
            {
                try
                {
                    _view.ReadArray(0, _bytes, 0, _size);
                }
                catch (Exception)
                {
                    Close();       // 게임이 내려가면서 매핑이 사라진 경우
                    return false;
                }

                T candidate = FromBytes(_bytes);
                if (versionBegin(candidate) == versionEnd(candidate))
                {
                    value = candidate;
                    return true;
                }
            }
            return false;          // 계속 쓰기 중 — 다음 틱에 다시 시도
        }

        private static T FromBytes(byte[] bytes)
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            }
            finally
            {
                handle.Free();
            }
        }

        public void Close()
        {
            if (_view != null)
            {
                _view.Dispose();
                _view = null;
            }
            if (_mmf != null)
            {
                _mmf.Dispose();
                _mmf = null;
            }
        }

        public void Dispose()
        {
            Close();
        }
    }

    /// <summary>
    /// "버전 카운터가 실제로 움직이나" 판정 — rF2 경로의 연결 규칙 (리더와 셀프테스트가 같이 쓴다).
    ///
    ///  - 버전이 바뀌면 그 시각을 기억하고, 5초 안에 바뀐 적이 있으면 연결.
    ///  - 처음 본 버전, 그리고 5초 넘게 못 보다가(읽지 않았거나 매핑이 없었거나) 다시 본 버전은
    ///    "바뀜"이 아니라 기준값이다 — 오래 안 읽은 리더가 멈춘 게임의 버퍼를 처음 읽으면 예전
    ///    버전과 달라 보여서 5초 동안 "연결"로 착각하던 문제를 막는다. 다음에 실제로 바뀌어야 연결.
    /// </summary>
    public sealed class VersionLiveness
    {
        public const double StaleAfterSeconds = 5.0;

        private bool _have;
        private int _version;
        private double _lastSeen = double.NegativeInfinity;
        private double _lastChange = double.NegativeInfinity;

        public bool Connected { get; private set; }

        /// <summary>실제로 바뀐 횟수 (기준값은 세지 않는다).</summary>
        public long Changes { get; private set; }

        /// <summary>한 번이라도 버전을 읽었는가.</summary>
        public bool Seen { get { return _have; } }

        public void Observe(int version, double now)
        {
            bool baseline = !_have || now - _lastSeen > StaleAfterSeconds;
            if (!baseline && version != _version)
            {
                _lastChange = now;
                Changes++;
            }
            _have = true;
            _version = version;
            _lastSeen = now;
            Connected = now - _lastChange <= StaleAfterSeconds;
        }

        /// <summary>이번에는 못 읽었다 (매핑 없음·쓰는 중).</summary>
        public void Missing(double now)
        {
            Connected = now - _lastChange <= StaleAfterSeconds;
        }
    }

    /// <summary>
    /// 스냅샷 없이 버전만 따라가는 원천 — TelemetrySource가 다른 원천(LMU 내장 메모리)을 쓰는
    /// 동안에도 매 틱 불러, 나중에 넘어올 때 멈춘 버퍼를 "방금 갱신됨"으로 착각하지 않게 한다.
    /// </summary>
    public interface IVersionTracker
    {
        /// <summary>버전만 읽어 연결 판정을 갱신한다 (가볍다).</summary>
        void TrackVersion();

        /// <summary>실제로 바뀐 횟수 — 이 값이 늘어야 "지금 움직이는" 원천이다.</summary>
        long VersionChanges { get; }
    }

    /// <summary>
    /// 텔레메트리/스코어링/Extended 세 버퍼를 묶어 읽고 연결 상태를 판단한다.
    ///
    /// 이름에 Rf2를 붙인 이유: SimHub의 GameReaderCommon에도 SharedMemoryReader가
    /// 있어서 플러그인 쪽에서 이름이 충돌한다.
    ///
    /// 연결 판정은 "버퍼가 열렸는가"가 아니라 "버전 카운터가 실제로 움직이는가"로
    /// 한다 (VersionLiveness). 게임이 종료돼도 매핑이 남아 있을 수 있기 때문 — 우리가 쥔
    /// 핸들도 이름 있는 매핑을 살려 두므로, 5초 넘게 멈춰 있으면 2초마다 세 버퍼를 닫았다가
    /// 다시 연다 (게임이 꺼졌으면 다시 열기가 실패해 정말 "없음"이 되고, 다음 게임 실행이 우리가
    /// 쥔 옛 매핑을 물려받지 않는다). 일시정지면 바로 다시 열려서 비용이 거의 없다.
    /// </summary>
    public sealed class Rf2SharedMemoryReader : ISnapshotSource, IVersionTracker
    {
        public const string TelemetryName = "$rFactor2SMMP_Telemetry$";
        public const string ScoringName = "$rFactor2SMMP_Scoring$";
        public const string ExtendedName = "$rFactor2SMMP_Extended$";

        private const double RecycleSeconds = 2.0;

        private readonly MappedBuffer<rF2Telemetry> _telemetry =
            new MappedBuffer<rF2Telemetry>(TelemetryName, RF2Sizes.rF2Telemetry);
        private readonly MappedBuffer<rF2Scoring> _scoring =
            new MappedBuffer<rF2Scoring>(ScoringName, RF2Sizes.rF2Scoring);
        private readonly MappedBuffer<rF2Extended> _extended =
            new MappedBuffer<rF2Extended>(ExtendedName, RF2Sizes.rF2Extended);

        private readonly VersionLiveness _live = new VersionLiveness();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastRecycle = double.NegativeInfinity;

        public bool Connected { get; private set; }

        public long VersionChanges { get { return _live.Changes; } }

        /// <summary>
        /// 생성된 C# 구조체 레이아웃이 ctypes 기준 크기와 일치하는지 검증.
        /// 하나라도 어긋나면 모든 필드가 깨지므로 시작 시 반드시 확인한다.
        /// </summary>
        public static string VerifyLayout()
        {
            var checks = new[]
            {
                new { Name = "rF2Telemetry", Actual = Marshal.SizeOf(typeof(rF2Telemetry)), Expected = RF2Sizes.rF2Telemetry },
                new { Name = "rF2Scoring", Actual = Marshal.SizeOf(typeof(rF2Scoring)), Expected = RF2Sizes.rF2Scoring },
                new { Name = "rF2Extended", Actual = Marshal.SizeOf(typeof(rF2Extended)), Expected = RF2Sizes.rF2Extended },
                new { Name = "rF2VehicleScoring", Actual = Marshal.SizeOf(typeof(rF2VehicleScoring)), Expected = RF2Sizes.rF2VehicleScoring },
                new { Name = "rF2VehicleTelemetry", Actual = Marshal.SizeOf(typeof(rF2VehicleTelemetry)), Expected = RF2Sizes.rF2VehicleTelemetry },
            };
            foreach (var c in checks)
            {
                if (c.Actual != c.Expected)
                    return string.Format("{0} 크기 불일치: {1} (기대 {2})", c.Name, c.Actual, c.Expected);
            }
            if (Marshal.OffsetOf(typeof(rF2Scoring), "mVersionUpdateBegin").ToInt32() != 0
                || Marshal.OffsetOf(typeof(rF2Scoring), "mVersionUpdateEnd").ToInt32() != 4)
                return "rF2Scoring 버전 카운터가 맨 앞 두 int가 아님 (TrackVersion 가정)";
            return null;   // 정상
        }

        /// <summary>세 버퍼를 읽어 스냅샷을 만든다. 데이터가 없으면 null.</summary>
        public Snapshot Poll()
        {
            double now = _clock.Elapsed.TotalSeconds;
            rF2Scoring scoring;
            if (!_scoring.TryRead(s => s.mVersionUpdateBegin, s => s.mVersionUpdateEnd, out scoring))
            {
                _live.Missing(now);
                Connected = _live.Connected;
                return null;
            }

            // 스코어링 버전이 계속 같으면 게임이 멈춘 것 (일시정지/종료)
            Observe(scoring.mVersionUpdateEnd, now);
            if (!Connected)
                return null;

            rF2Telemetry telemetry;
            _telemetry.TryRead(t => t.mVersionUpdateBegin, t => t.mVersionUpdateEnd, out telemetry);

            rF2Extended extended;
            bool hasExtended = _extended.TryRead(
                e => e.mVersionUpdateBegin, e => e.mVersionUpdateEnd, out extended);

            return SnapshotBuilder.Build(scoring, telemetry, hasExtended, extended);
        }

        /// <summary>스코어링 버전 카운터만 읽어 연결 판정을 갱신한다 (스냅샷 없음, 8바이트).</summary>
        public void TrackVersion()
        {
            double now = _clock.Elapsed.TotalSeconds;
            int version;
            if (_scoring.TryReadVersion(out version))
            {
                Observe(version, now);
            }
            else
            {
                _live.Missing(now);
                Connected = _live.Connected;
            }
        }

        private void Observe(int version, double now)
        {
            _live.Observe(version, now);
            Connected = _live.Connected;
            if (!Connected && now - _lastRecycle >= RecycleSeconds)
            {
                // 멈춘 매핑을 쥐고 있지 않는다 — 다음 읽기에서 다시 연다 (게임이 꺼졌으면 실패)
                _lastRecycle = now;
                _telemetry.Close();
                _scoring.Close();
                _extended.Close();
            }
        }

        /// <summary>
        /// Extended 버퍼만 읽는다 (버전 일치만 확인 — 살아 있는지는 보지 않는다). 없으면 false.
        /// </summary>
        public bool TryReadExtended(out rF2Extended extended)
        {
            return _extended.TryRead(e => e.mVersionUpdateBegin, e => e.mVersionUpdateEnd, out extended);
        }

        /// <summary>
        /// LMU 내장 메모리 경로가 거기 없는 세 필드(피트 제한속도, 상태/히스토리 메시지)를 보충할 때
        /// 쓴다 (TelemetrySource 참고). DLL이 **이번 게임 실행에서 살아 있을 때만** — 스코어링 버전이
        /// 5초 안에 움직였을 때만(Poll과 같은 규칙) Extended를 읽는다. 멈춘 버퍼(DLL을 지운 뒤 남은
        /// 예전 실행의 매핑, 종료 때 0으로 지운 버퍼)를 "DLL에서 보충"으로 쓰지 않는다.
        /// </summary>
        public bool TryReadLiveExtended(out rF2Extended extended)
        {
            extended = default(rF2Extended);
            TrackVersion();
            if (!Connected)
                return false;
            return TryReadExtended(out extended);
        }

        public void Dispose()
        {
            _telemetry.Dispose();
            _scoring.Dispose();
            _extended.Dispose();
        }
    }
}
