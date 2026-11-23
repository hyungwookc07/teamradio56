using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace TeamRadio56.Core.Telemetry
{
    /// <summary>
    /// 읽기 전용 메모리 창 하나. 실제로는 이름 있는 파일 매핑(MappedLmuMemory), 테스트에선
    /// 바이트 배열 — 리눅스 테스트 러너에서도 리더 로직 전체를 돌릴 수 있게 나눴다.
    /// </summary>
    public interface ILmuMemory : IDisposable
    {
        /// <summary>열려 있거나 지금 열었으면 true. 매핑이 없으면 false (조용히).</summary>
        bool TryOpen();

        /// <summary>열린 창의 바이트 수 (닫혀 있으면 0).</summary>
        long Capacity { get; }

        /// <summary>offset부터 count 바이트를 buffer[index..]로. 실패(매핑 사라짐 등)면 false.</summary>
        bool Read(long offset, byte[] buffer, int index, int count);

        void Close();
    }

    /// <summary>
    /// 이름 있는 파일 매핑을 읽기 전용으로 연다 — OpenExisting만 쓰고 절대 만들지 않는다
    /// (만들면 0으로 채운 가짜 매핑이 생겨 "게임 실행 중"으로 착각한다).
    /// 창 크기는 0(매핑 전체)으로 열어 C++ 꼬리 패딩이 있어도 된다. 크기 검사는 리더가 한다.
    /// </summary>
    public sealed class MappedLmuMemory : ILmuMemory
    {
        // 매핑이 없을 때(DLL만 쓰는 사용자, LMU 1.2 미만, 설정 꺼짐) 틱마다 예외를 내지 않게
        private const int RetryMs = 1000;

        private readonly string _name;
        private MemoryMappedFile _mmf;
        private MemoryMappedViewAccessor _view;
        private bool _failed;
        private int _lastFailTick;

        public MappedLmuMemory(string name)
        {
            _name = name;
        }

        public long Capacity
        {
            get { return _view != null ? _view.Capacity : 0; }
        }

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
                _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _failed = false;
                return true;
            }
            catch (Exception)
            {
                Close();
                _failed = true;
                _lastFailTick = now;
                return false;
            }
        }

        public bool Read(long offset, byte[] buffer, int index, int count)
        {
            if (_view == null)
                return false;
            try
            {
                return _view.ReadArray(offset, buffer, index, count) == count;
            }
            catch (Exception)
            {
                Close();
                return false;
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

    /// <summary>rF2 플러그인의 Extended 버퍼를 읽는 함수 — 내장 메모리에 없는 필드 보충용.</summary>
    public delegate bool ExtendedReader(out rF2Extended extended);

    /// <summary>내장 메모리 리더의 마지막 판정 (진단·로그용).</summary>
    public enum LmuNativeState
    {
        /// <summary>LMU_Data 매핑이 없다 — 게임 꺼짐, Enable Plugins 꺼짐/재시작 전, LMU 1.2 미만.</summary>
        Absent,
        /// <summary>매핑이 기대(324820 B)보다 작다 — 다른 형식.</summary>
        TooSmall,
        /// <summary>gameVersion이 0 이하 — 게임이 아직/더 이상 쓰지 않는다.</summary>
        NotRunning,
        /// <summary>차량 수 범위 밖·값이 유한하지 않음 — 이 프레임은 버린다.</summary>
        Invalid,
        /// <summary>복사 중에 게임이 써서 감시 값이 바뀌었다 (재시도 모두 실패) — 이 프레임은 버린다.</summary>
        Torn,
        /// <summary>mCurrentET·SME_UPDATE_SCORING이 5초 넘게 그대로 — 일시정지/종료.</summary>
        Stale,
        /// <summary>정상 프레임.</summary>
        Live,
    }

    /// <summary>
    /// LMU 내장 공유 메모리(LMU_Data) 리더 — rF2 DLL 없이 같은 Snapshot을 만든다.
    ///
    /// 읽기 규약 (게임이 제자리에 쓰고, rF2 플러그인 같은 버전 카운터·뮤텍스·이벤트가 없다):
    ///  1. 감시 값(witness)을 읽는다: gameVersion, mCurrentET, mNumVehicles, 텔레메트리 헤더
    ///     (activeVehicles·playerVehicleIdx·playerHasVehicle). SME_UPDATE_SCORING/TELEMETRY도 함께
    ///     읽지만 값의 의미가 확인되지 않아 비교에는 넣지 않고 "바뀌면 살아 있음"에만 쓴다
    ///     (락으로 쓰지 않는다 — 매 틱 바뀌는 값이면 모든 프레임이 찢김으로 버려질 수 있다).
    ///  2. 필요한 범위만 복사한다: ScoringInfo, 차량 n대의 VehicleScoring, 텔레메트리 헤더,
    ///     플레이어의 VehicleTelemetry 슬롯 하나.
    ///  3. 감시 값과 플레이어 슬롯 앞 24바이트(mID·mDeltaTime·mElapsedTime)를 다시 읽어 원시
    ///     비트를 비교한다. 다르면 다시(최대 4번). 다 실패하면 이 프레임은 버리고 직전 정상
    ///     프레임으로 스냅샷을 다시 만든다 (일시정지 때처럼 값만 같은 프레임 — 엔진은 null을
    ///     연결 끊김으로 받아 세션을 초기화하므로 null을 내지 않는다). 최선 노력일 뿐 완전한
    ///     일관성은 아니다.
    ///  - 차량 수 일치(mNumVehicles == activeVehicles)는 요구하지 않는다 (차가 들어올 때 잠깐 다름).
    ///  - 스코어링 배열과 텔레메트리 배열은 인덱스가 맞지 않는다: 플레이어 슬롯은 mID로 찾고,
    ///    playerVehicleIdx는 mID가 맞을 때만 쓰는 빠른 길이다.
    ///
    /// 연결: 매핑 열림 + gameVersion &gt; 0 + mCurrentET나 SME_UPDATE_SCORING이 5초 안에 바뀜
    ///       + 5초 안에 정상 프레임 (rF2 경로의 "스코어링 버전이 움직이나"와 같은 규칙).
    ///
    /// 값 검증(가드): 처음으로 플레이어가 차고 밖에 있는 프레임에서 플레이어 슬롯 mID 일치,
    /// 타이어 온도 200~600 K, 공기압 0~1000 kPa, 마모 0~1, 모두 유한한지 본다. 3프레임 연속
    /// 어긋나면(또는 플레이어 슬롯을 25프레임 못 찾으면) 형식이 바뀐 것으로 보고 GuardFailed를
    /// 걸어 쓰지 않는다 (TelemetrySource가 rF2 DLL로 대체하고 크게 로그). 한 번 실패한 뒤에는
    /// 매핑을 다시 열어도 검증을 통과해야만 다시 쓴다.
    /// </summary>
    public sealed class LmuNativeReader : ISnapshotSource
    {
        public const double StaleAfterSeconds = 5.0;
        public const int ReadRetries = 4;
        public const int GuardBadFramesToFail = 3;
        public const int GuardMissingSlotFramesToFail = 25;

        private const int WitnessSize = 27;
        private const int WitnessCompareFrom = 8;     // gameVersion부터 (SME_* 제외)
        private const int SlotPrefix = 24;

        private readonly ILmuMemory _mem;
        private readonly Func<double> _clock;
        private readonly ExtendedReader _extended;

        private readonly byte[] _info = new byte[LmuNativeLayout.ScoringInfoSize];
        private readonly byte[] _veh = new byte[LmuNativeLayout.MaxVehicles * LmuNativeLayout.VehicleScoringStride];
        private readonly byte[] _hdr = new byte[3];
        private readonly byte[] _slotBuf = new byte[LmuNativeLayout.VehicleTelemetryStride];
        private readonly byte[] _w1 = new byte[WitnessSize];
        private readonly byte[] _w2 = new byte[WitnessSize];
        private readonly byte[] _prefix = new byte[SlotPrefix];
        private readonly byte[] _id4 = new byte[4];

        // 이번 복사의 결과
        private int _n;
        private int _active;
        private int _playerIdx;
        private int _slot;

        private bool _open;
        private bool _haveLive;
        private long _lastEtBits;
        private uint _lastSme;
        private double _lastChange = double.NegativeInfinity;
        private double _lastGood = double.NegativeInfinity;
        private int _lastGameVersion;

        private bool _haveLast;
        private rF2Scoring _lastScoring;
        private rF2Telemetry _lastTelemetry;
        private bool _lastHasExt;
        private rF2Extended _lastExt;

        private bool _everFailed;
        private int _badFrames;
        private int _missingSlotFrames;

        /// <param name="memory">LMU_Data 창.</param>
        /// <param name="clock">초 단위 단조 시계 (테스트용; null이면 실제 시계).</param>
        /// <param name="extended">rF2 Extended 보충 (없으면 null — 피트 제한속도 0, 메시지 빈 문자열).</param>
        public LmuNativeReader(ILmuMemory memory, Func<double> clock = null, ExtendedReader extended = null)
        {
            _mem = memory;
            _extended = extended;
            if (clock == null)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                clock = () => watch.Elapsed.TotalSeconds;
            }
            _clock = clock;
            State = LmuNativeState.Absent;
        }

        public bool Connected { get; private set; }
        public LmuNativeState State { get; private set; }
        /// <summary>마지막으로 읽은 gameVersion 원시값 (인코딩 미확정 — 로그용).</summary>
        public int GameVersion { get; private set; }
        /// <summary>값 검증을 통과했는가 (플레이어가 차고 밖에 있는 프레임에서).</summary>
        public bool Verified { get; private set; }
        /// <summary>값 검증 실패 — 형식이 바뀐 것으로 보고 이 매핑을 쓰지 않는다.</summary>
        public bool GuardFailed { get; private set; }
        public string GuardError { get; private set; }
        /// <summary>마지막 정상 프레임이 rF2 Extended로 보충됐는가.</summary>
        public bool HasExtended { get; private set; }
        /// <summary>마지막 Invalid 판정 사유.</summary>
        public string InvalidReason { get; private set; }
        /// <summary>찢긴 읽기 횟수 — 재시도 전부 실패해 버린 프레임 (진단용).</summary>
        public int TornFrames { get; private set; }
        /// <summary>찢김으로 다시 읽은 횟수 (재시도 성공 포함, 진단용).</summary>
        public int Retries { get; private set; }

        /// <summary>
        /// 지금 이 소스를 써도 되는가: 연결됨 + 가드 통과(실패 없음). 예전에 실패한 적이 있으면
        /// 검증을 다시 통과해야 한다.
        /// </summary>
        public bool Usable
        {
            get { return Connected && !GuardFailed && (Verified || !_everFailed); }
        }

        public Snapshot Poll()
        {
            double now = _clock();
            if (!_mem.TryOpen())
            {
                Lost(LmuNativeState.Absent);
                return null;
            }
            if (!_open)
            {
                _open = true;
                ResetSession();
            }
            if (_mem.Capacity < LmuNativeLayout.TotalSize)
            {
                InvalidReason = "매핑 크기 " + _mem.Capacity + " B < " + LmuNativeLayout.TotalSize;
                _mem.Close();
                Lost(LmuNativeState.TooSmall);
                return null;
            }

            bool stable = false;
            for (int attempt = 0; attempt < ReadRetries; attempt++)
            {
                if (attempt > 0)
                    Retries++;
                if (!ReadWitness(_w1) || !CopyFrame() || !ReadWitness(_w2))
                {
                    _mem.Close();          // 게임이 내려가며 매핑이 사라진 경우
                    Lost(LmuNativeState.Absent);
                    return null;
                }
                // SME_* (앞 8바이트)는 의미가 미확정이라 비교하지 않는다 — 활성 판정에만 쓴다
                bool same = Same(_w1, _w2, WitnessCompareFrom, WitnessSize - WitnessCompareFrom);
                if (same && _slot >= 0)
                {
                    if (!_mem.Read(LmuNativeLayout.VehicleTelemetry(_slot), _prefix, 0, SlotPrefix))
                    {
                        _mem.Close();
                        Lost(LmuNativeState.Absent);
                        return null;
                    }
                    same = Same(_prefix, _slotBuf, 0, SlotPrefix);
                }
                if (same)
                {
                    stable = true;
                    break;
                }
            }

            GameVersion = BitConverter.ToInt32(_w2, 8);
            if (_lastGameVersion != 0 && GameVersion != _lastGameVersion && GameVersion > 0)
                ResetGuard();      // 게임 업데이트/재시작 — 형식 판정을 새로
            if (GameVersion > 0)
                _lastGameVersion = GameVersion;
            if (GameVersion <= 0)
            {
                State = LmuNativeState.NotRunning;
                Connected = false;
                _haveLive = false;
                return null;
            }

            // 활성 판정 — 찢긴 프레임이라도 두 번째 감시 값은 실제로 읽은 값이다
            long etBits = BitConverter.ToInt64(_w2, 12);
            uint sme = BitConverter.ToUInt32(_w2, 0);
            if (!_haveLive || etBits != _lastEtBits || sme != _lastSme)
            {
                _haveLive = true;
                _lastEtBits = etBits;
                _lastSme = sme;
                _lastChange = now;
            }
            bool alive = now - _lastChange <= StaleAfterSeconds;

            rF2Scoring scoring = default(rF2Scoring);
            rF2Telemetry telemetry = default(rF2Telemetry);
            bool hasExt = false;
            rF2Extended ext = default(rF2Extended);
            bool good = false;
            if (!stable)
            {
                TornFrames++;
                State = LmuNativeState.Torn;
            }
            else
            {
                string reason;
                if (Assemble(out scoring, out telemetry, out reason))
                {
                    good = true;
                    State = LmuNativeState.Live;
                    _lastGood = now;
                    hasExt = TryExtended(out ext);
                    HasExtended = hasExt;
                    _haveLast = true;
                    _lastScoring = scoring;
                    _lastTelemetry = telemetry;
                    _lastHasExt = hasExt;
                    _lastExt = ext;
                    CheckGuard(scoring, telemetry);
                }
                else
                {
                    InvalidReason = reason;
                    State = LmuNativeState.Invalid;
                }
            }

            Connected = alive && now - _lastGood <= StaleAfterSeconds;
            if (!alive)
                State = LmuNativeState.Stale;
            if (!Usable)
                return null;
            if (good)
                return SnapshotBuilder.Build(scoring, telemetry, hasExt, ext);
            // 이번 프레임만 버림 — 직전 정상 프레임으로 (값은 같고 시각만 새로)
            return _haveLast
                ? SnapshotBuilder.Build(_lastScoring, _lastTelemetry, _lastHasExt, _lastExt)
                : null;
        }

        private void Lost(LmuNativeState state)
        {
            State = state;
            Connected = false;
            _open = false;
            _haveLive = false;
        }

        /// <summary>매핑을 새로 열었다 — 게임 재시작일 수 있으니 상태를 비운다.</summary>
        private void ResetSession()
        {
            _haveLast = false;
            _lastGood = double.NegativeInfinity;
            _lastChange = double.NegativeInfinity;
            _haveLive = false;
            _lastGameVersion = 0;
            ResetGuard();
        }

        private void ResetGuard()
        {
            Verified = false;
            GuardFailed = false;
            GuardError = null;
            _badFrames = 0;
            _missingSlotFrames = 0;
        }

        private bool ReadWitness(byte[] w)
        {
            return _mem.Read(LmuNativeLayout.EventUpdateScoring, w, 0, 8)      // SME_UPDATE_SCORING + TELEMETRY
                && _mem.Read(LmuNativeLayout.GameVersion, w, 8, 4)
                && _mem.Read(LmuNativeLayout.CurrentEt, w, 12, 8)
                && _mem.Read(LmuNativeLayout.NumVehicles, w, 20, 4)
                && _mem.Read(LmuNativeLayout.ActiveVehicles, w, 24, 3);
        }

        /// <summary>필요한 범위만 복사하고 플레이어 슬롯을 찾는다. 읽기 실패면 false.</summary>
        private bool CopyFrame()
        {
            _playerIdx = -1;
            _slot = -1;
            if (!_mem.Read(LmuNativeLayout.ScoringInfoOffset, _info, 0, LmuNativeLayout.ScoringInfoSize))
                return false;
            _n = BitConverter.ToInt32(_info, LmuNativeLayout.NumVehicles - LmuNativeLayout.ScoringInfoOffset);
            int n = _n >= 0 && _n <= LmuNativeLayout.MaxVehicles ? _n : 0;
            if (n > 0 && !_mem.Read(LmuNativeLayout.VehicleScoringOffset, _veh, 0,
                                    n * LmuNativeLayout.VehicleScoringStride))
                return false;
            if (!_mem.Read(LmuNativeLayout.ActiveVehicles, _hdr, 0, 3))
                return false;
            _active = _hdr[0];

            // 스코어링 플레이어 — SnapshotBuilder와 같이 mIsPlayer != 0 중 마지막
            for (int i = 0; i < n; i++)
            {
                if (_veh[i * LmuNativeLayout.VehicleScoringStride + LmuNativeLayout.ScoringIsPlayerOffset] != 0)
                    _playerIdx = i;
            }
            if (_playerIdx < 0)
                return true;
            int playerId = BitConverter.ToInt32(_veh,
                _playerIdx * LmuNativeLayout.VehicleScoringStride + LmuNativeLayout.ScoringIdOffset);

            int count = Math.Min(_active, LmuNativeLayout.MaxVehicles);
            // 빠른 길: playerVehicleIdx — mID가 맞을 때만
            if (_hdr[2] != 0 && _hdr[1] < count)
            {
                if (!_mem.Read(LmuNativeLayout.VehicleTelemetry(_hdr[1]) + LmuNativeLayout.TelIdOffset, _id4, 0, 4))
                    return false;
                if (BitConverter.ToInt32(_id4, 0) == playerId)
                    _slot = _hdr[1];
            }
            for (int i = 0; _slot < 0 && i < count; i++)
            {
                if (!_mem.Read(LmuNativeLayout.VehicleTelemetry(i) + LmuNativeLayout.TelIdOffset, _id4, 0, 4))
                    return false;
                if (BitConverter.ToInt32(_id4, 0) == playerId)
                    _slot = i;
            }
            if (_slot >= 0 && !_mem.Read(LmuNativeLayout.VehicleTelemetry(_slot), _slotBuf, 0,
                                         LmuNativeLayout.VehicleTelemetryStride))
                return false;
            return true;
        }

        /// <summary>
        /// 복사한 조각을 기존 rF2 구조체로 마샬링해 rF2 버퍼 모양으로 조립한다 — 그러면
        /// SnapshotBuilder가 rF2 경로와 똑같이 변환한다 (단위·의미가 구성상 같다).
        /// </summary>
        private bool Assemble(out rF2Scoring scoring, out rF2Telemetry telemetry, out string reason)
        {
            scoring = default(rF2Scoring);
            telemetry = default(rF2Telemetry);
            if (_n < 0 || _n > LmuNativeLayout.MaxVehicles)
            {
                reason = "mNumVehicles " + _n + " (0~" + LmuNativeLayout.MaxVehicles + " 밖)";
                return false;
            }
            if (_active > LmuNativeLayout.MaxVehicles)
            {
                reason = "activeVehicles " + _active + " (0~" + LmuNativeLayout.MaxVehicles + " 밖)";
                return false;
            }
            rF2ScoringInfo info = Parse<rF2ScoringInfo>(_info, 0);
            if (!Finite(info.mCurrentET) || !Finite(info.mEndET) || !Finite(info.mLapDist))
            {
                reason = "ScoringInfo 시간/트랙 길이가 유한하지 않음";
                return false;
            }
            info.mNumVehicles = _n;
            var vehicles = new rF2VehicleScoring[128];
            for (int i = 0; i < _n; i++)
                vehicles[i] = Parse<rF2VehicleScoring>(_veh, i * LmuNativeLayout.VehicleScoringStride);
            scoring = new rF2Scoring { mScoringInfo = info, mVehicles = vehicles };

            if (_slot >= 0)
            {
                telemetry = new rF2Telemetry
                {
                    mNumVehicles = 1,
                    mVehicles = new[] { Parse<rF2VehicleTelemetry>(_slotBuf, 0) },
                };
            }
            else
            {
                telemetry = new rF2Telemetry { mNumVehicles = 0, mVehicles = new rF2VehicleTelemetry[0] };
            }
            reason = null;
            return true;
        }

        private bool TryExtended(out rF2Extended ext)
        {
            ext = default(rF2Extended);
            if (_extended == null)
                return false;
            try
            {
                return _extended(out ext);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void CheckGuard(rF2Scoring scoring, rF2Telemetry telemetry)
        {
            if (Verified || GuardFailed || _playerIdx < 0)
                return;                       // 플레이어 없음(관전·메뉴) — 판정 보류
            rF2VehicleScoring me = scoring.mVehicles[_playerIdx];
            if (me.mInGarageStall != 0)
                return;                       // 차고 안 — 판정 보류
            if (_slot < 0)
            {
                if (++_missingSlotFrames >= GuardMissingSlotFramesToFail)
                    Fail("플레이어(mID " + me.mID + ") 텔레메트리 슬롯을 " + _missingSlotFrames
                         + "프레임 동안 못 찾음 (activeVehicles " + _active + ")");
                return;
            }
            _missingSlotFrames = 0;
            string bad = CheckPlayer(me, telemetry.mVehicles[0]);
            if (bad == null)
            {
                Verified = true;
                _badFrames = 0;
            }
            else if (++_badFrames >= GuardBadFramesToFail)
            {
                Fail(bad);
            }
        }

        /// <summary>플레이어 텔레메트리가 그럴듯한가 — 어긋나면 사유, 정상이면 null.</summary>
        public static string CheckPlayer(rF2VehicleScoring me, rF2VehicleTelemetry vt)
        {
            if (vt.mID != me.mID)
                return "플레이어 mID 불일치 (스코어링 " + me.mID + " / 텔레메트리 " + vt.mID + ")";
            if (!Finite(vt.mFuel) || vt.mFuel < 0 || !Finite(vt.mEngineRPM)
                || !Finite(vt.mLocalVel.x) || !Finite(vt.mLocalVel.y) || !Finite(vt.mLocalVel.z))
                return "연료/RPM/속도 값이 이상함";
            if (vt.mWheels == null || vt.mWheels.Length != 4)
                return "휠 배열 없음";
            for (int w = 0; w < 4; w++)
            {
                rF2Wheel wh = vt.mWheels[w];
                for (int j = 0; j < 3; j++)
                {
                    double t = wh.mTemperature[j];
                    if (!Finite(t) || t < 200 || t > 600)
                        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "휠{0} 타이어 온도 {1:0.##} K (200~600 밖)", w, t);
                }
                if (!Finite(wh.mPressure) || wh.mPressure < 0 || wh.mPressure > 1000)
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "휠{0} 공기압 {1:0.##} kPa (0~1000 밖)", w, wh.mPressure);
                if (!Finite(wh.mWear) || wh.mWear < -0.001 || wh.mWear > 1.001)
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "휠{0} 마모 {1:0.###} (0~1 밖)", w, wh.mWear);
                if (!Finite(wh.mTireCarcassTemperature) || !Finite(wh.mBrakeTemp))
                    return "휠" + w + " 카커스/브레이크 온도가 유한하지 않음";
            }
            return null;
        }

        private void Fail(string reason)
        {
            GuardFailed = true;
            GuardError = reason;
            _everFailed = true;
        }

        private static bool Same(byte[] a, byte[] b, int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        private static bool Finite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static T Parse<T>(byte[] bytes, int offset) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return (T)Marshal.PtrToStructure(IntPtr.Add(handle.AddrOfPinnedObject(), offset), typeof(T));
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            _mem.Dispose();
        }
    }
}
