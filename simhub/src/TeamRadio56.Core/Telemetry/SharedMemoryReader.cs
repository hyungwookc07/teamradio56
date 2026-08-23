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

        private readonly string _name;
        private readonly int _size;
        private readonly byte[] _bytes;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;

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
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(_name, MemoryMappedFileRights.Read);
                _view = _mmf.CreateViewAccessor(0, _size, MemoryMappedFileAccess.Read);
                return true;
            }
            catch (Exception)
            {
                // 게임 미실행/플러그인 비활성 — 정상 상황이므로 조용히 실패
                Close();
                return false;
            }
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
    /// 텔레메트리/스코어링/Extended 세 버퍼를 묶어 읽고 연결 상태를 판단한다.
    ///
    /// 연결 판정은 "버퍼가 열렸는가"가 아니라 "버전 카운터가 실제로 움직이는가"로
    /// 한다. 게임이 종료돼도 매핑이 잠시 남아 있을 수 있기 때문.
    /// </summary>
    public sealed class SharedMemoryReader : IDisposable
    {
        public const string TelemetryName = "$rFactor2SMMP_Telemetry$";
        public const string ScoringName = "$rFactor2SMMP_Scoring$";
        public const string ExtendedName = "$rFactor2SMMP_Extended$";

        private const double StaleAfterSeconds = 5.0;

        private readonly MappedBuffer<rF2Telemetry> _telemetry =
            new MappedBuffer<rF2Telemetry>(TelemetryName, RF2Sizes.rF2Telemetry);
        private readonly MappedBuffer<rF2Scoring> _scoring =
            new MappedBuffer<rF2Scoring>(ScoringName, RF2Sizes.rF2Scoring);
        private readonly MappedBuffer<rF2Extended> _extended =
            new MappedBuffer<rF2Extended>(ExtendedName, RF2Sizes.rF2Extended);

        private int _lastVersion = -1;
        private DateTime _lastChange = DateTime.MinValue;

        public bool Connected { get; private set; }

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
            return null;   // 정상
        }

        /// <summary>세 버퍼를 읽어 스냅샷을 만든다. 데이터가 없으면 null.</summary>
        public Snapshot Poll()
        {
            rF2Scoring scoring;
            if (!_scoring.TryRead(s => s.mVersionUpdateBegin, s => s.mVersionUpdateEnd, out scoring))
            {
                MarkStale();
                return null;
            }

            // 스코어링 버전이 계속 같으면 게임이 멈춘 것 (일시정지/종료)
            int version = scoring.mVersionUpdateEnd;
            DateTime now = DateTime.UtcNow;
            if (version != _lastVersion)
            {
                _lastVersion = version;
                _lastChange = now;
                Connected = true;
            }
            else if ((now - _lastChange).TotalSeconds > StaleAfterSeconds)
            {
                Connected = false;
            }

            if (!Connected)
                return null;

            rF2Telemetry telemetry;
            _telemetry.TryRead(t => t.mVersionUpdateBegin, t => t.mVersionUpdateEnd, out telemetry);

            rF2Extended extended;
            bool hasExtended = _extended.TryRead(
                e => e.mVersionUpdateBegin, e => e.mVersionUpdateEnd, out extended);

            return SnapshotBuilder.Build(scoring, telemetry, hasExtended, extended);
        }

        private void MarkStale()
        {
            if ((DateTime.UtcNow - _lastChange).TotalSeconds > StaleAfterSeconds)
                Connected = false;
        }

        public void Dispose()
        {
            _telemetry.Dispose();
            _scoring.Dispose();
            _extended.Dispose();
        }
    }
}
