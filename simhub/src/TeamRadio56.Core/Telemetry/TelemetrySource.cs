using System;

namespace TeamRadio56.Core.Telemetry
{
    /// <summary>Snapshot을 내는 원천 하나 (rF2 플러그인 버퍼, LMU 내장 메모리).</summary>
    public interface ISnapshotSource : IDisposable
    {
        /// <summary>한 틱 읽기. 데이터가 없으면 null.</summary>
        Snapshot Poll();

        /// <summary>게임이 살아서 값을 갱신하고 있는가 (5초 기준).</summary>
        bool Connected { get; }
    }

    public enum TelemetrySourceKind
    {
        None,
        /// <summary>LMU 1.2+ 내장 공유 메모리 "LMU_Data" — rF2 DLL 불필요.</summary>
        LmuNative,
        /// <summary>rF2 Shared Memory Map Plugin (rFactor2SharedMemoryMapPlugin64.dll).</summary>
        Rf2Plugin,
    }

    /// <summary>
    /// 텔레메트리 원천 선택 — 플러그인 Tick이 쓰는 Poll()/Connected 표면은 예전 리더와 같다.
    ///
    /// 매 틱:
    ///  1. LMU 내장 메모리(LMU_Data)가 쓸 만하면(열림·gameVersion &gt; 0·차량 수 범위 안·값 검증
    ///     실패 없음·5초 안에 갱신) 그것을 쓴다. rF2 Extended 버퍼도 열려 있으면 내장 메모리에
    ///     없는 세 필드만 거기서 보충한다: Session.PitSpeedLimitKmh, StatusMessage,
    ///     HistoryMessage (없으면 0 / 빈 문자열 — rF2 경로에서 Extended가 없을 때와 같다).
    ///     그 밖의 모든 필드는 내장 메모리에서 온다.
    ///  2. 아니면 rF2 플러그인 경로를 예전 그대로 쓴다 (DLL만 있는 사용자는 동작이 같다).
    ///  3. 둘 다 아니면 연결 없음 ("게임 대기").
    /// 쓰던 원천은 그 원천이 5초 동안 멈추거나 사라질 때까지 유지된다 (각 리더의 연결 판정이
    /// 5초 기준이라 깜빡이지 않는다). 내장 메모리가 다시 쓸 만해지면 그쪽이 우선이다.
    /// 원천이 바뀔 때마다 한 번 로그를 남긴다.
    ///
    /// 진단: 내장 메모리를 쓰는 중에 rF2 DLL 버퍼도 있으면, 값 검증을 통과하고 5초쯤 뒤 한 번
    /// rF2 버퍼도 읽어 두 스냅샷을 대조한 결과를 로그에 남긴다 (SnapshotCompare — 엔진에는 넘기지
    /// 않는다). 실차에서 두 경로가 같은 값을 내는지 확인하는 용도.
    /// </summary>
    public sealed class TelemetrySource : IDisposable
    {
        private readonly LmuNativeReader _native;
        private readonly ISnapshotSource _rf2;
        private readonly Action<string> _info;
        private readonly Action<string> _warn;

        private bool _nativeWasConnected;
        private bool _nativeWasVerified;
        private bool _nativeWasFailed;
        private LmuNativeState _nativeLastState = LmuNativeState.Absent;

        /// <summary>내장 메모리 사용 몇 틱 뒤 rF2 버퍼와 한 번 대조할지 (5Hz 기준 약 5초).</summary>
        public const int CrossCheckAfterPolls = 25;
        private int _crossCheckPolls;
        private bool _crossChecked;

        public TelemetrySource(LmuNativeReader native, ISnapshotSource rf2,
                               Action<string> info, Action<string> warn)
        {
            _native = native;
            _rf2 = rf2;
            _info = info ?? (_ => { });
            _warn = warn ?? (_ => { });
            NativeEnabled = native != null;
        }

        /// <summary>실제 게임용: LMU_Data + rF2 플러그인 버퍼 (Extended 보충은 rF2 리더가 연다).</summary>
        public static TelemetrySource CreateDefault(Action<string> info, Action<string> warn)
        {
            var rf2 = new Rf2SharedMemoryReader();
            var native = new LmuNativeReader(
                new MappedLmuMemory(LmuNativeLayout.MappingName), null, rf2.TryReadExtended);
            return new TelemetrySource(native, rf2, info, warn);
        }

        /// <summary>false면 내장 메모리를 보지 않는다 (레이아웃 자체 검사 실패 시).</summary>
        public bool NativeEnabled { get; set; }

        public bool Connected { get; private set; }

        public TelemetrySourceKind Active { get; private set; }

        /// <summary>내장 메모리를 쓰면서 rF2 Extended로 세 필드를 보충하는 중인가.</summary>
        public bool GapFill { get; private set; }

        /// <summary>내장 메모리 값 검증 실패 (형식이 바뀐 듯 — 쓰지 않는 중).</summary>
        public bool NativeGuardFailed
        {
            get { return _native != null && NativeEnabled && _native.GuardFailed; }
        }

        public LmuNativeReader Native { get { return _native; } }

        /// <summary>
        /// LMU_Data 매핑은 있는데 아직/더 이상 갱신이 없는가 (메뉴·로딩·일시정지) — 설정 화면이
        /// "Enable Plugins를 켜라" 대신 "세션에 들어가면 연결" 안내를 고르는 데 쓴다.
        /// </summary>
        public bool NativeIdle
        {
            get
            {
                return _native != null && NativeEnabled && !_native.GuardFailed
                    && (_native.State == LmuNativeState.NotRunning || _native.State == LmuNativeState.Stale);
            }
        }

        public Snapshot Poll()
        {
            Snapshot snap = null;
            TelemetrySourceKind next = TelemetrySourceKind.None;

            if (_native != null && NativeEnabled)
            {
                Snapshot ns = _native.Poll();
                ObserveNative();
                if (_native.Usable)
                {
                    next = TelemetrySourceKind.LmuNative;
                    snap = ns;
                }
            }
            if (next == TelemetrySourceKind.None)
            {
                Snapshot rs = _rf2.Poll();
                if (_rf2.Connected)
                {
                    next = TelemetrySourceKind.Rf2Plugin;
                    snap = rs;
                }
            }

            bool gap = next == TelemetrySourceKind.LmuNative && _native.HasExtended;
            if (next != Active)
            {
                Active = next;
                _info(Describe(next, gap));
            }
            else if (next == TelemetrySourceKind.LmuNative && gap != GapFill)
            {
                _info(gap
                    ? "rF2 Extended 버퍼 발견 — 피트 제한속도·페널티 메시지를 거기서 보충"
                    : "rF2 Extended 버퍼 없음 — 피트 제한속도는 기본값, 페널티 메시지 없음");
            }
            GapFill = gap;
            Connected = next != TelemetrySourceKind.None;

            if (next != TelemetrySourceKind.LmuNative)
            {
                _crossChecked = false;
                _crossCheckPolls = 0;
            }
            else if (!_crossChecked && snap != null && _native.Verified
                     && ++_crossCheckPolls >= CrossCheckAfterPolls)
            {
                _crossChecked = true;
                CrossCheck(snap);
            }
            return snap;
        }

        /// <summary>진단 전용 — rF2 버퍼를 한 번 읽어 내장 스냅샷과 대조하고 로그만 남긴다.</summary>
        private void CrossCheck(Snapshot native)
        {
            Snapshot rf2;
            try
            {
                rf2 = _rf2.Poll();
            }
            catch (Exception)
            {
                rf2 = null;
            }
            if (rf2 == null || !_rf2.Connected)
            {
                _info("교차 확인 생략 — rF2 DLL 버퍼 없음 (LMU 내장 공유 메모리만 사용)");
                return;
            }
            System.Collections.Generic.List<string> diff = SnapshotCompare.Diff(native, rf2);
            if (diff.Count == 0)
                _info("교차 확인: LMU 내장 공유 메모리 = rF2 DLL 버퍼 (트랙·차량·플레이어·타이어 일치)");
            else
                _warn("교차 확인: LMU 내장 공유 메모리와 rF2 DLL 버퍼가 다름 " + diff.Count + "건 (내장/rF2) — "
                      + string.Join("; ", diff.ToArray()));
        }

        private string Describe(TelemetrySourceKind kind, bool gap)
        {
            switch (kind)
            {
                case TelemetrySourceKind.LmuNative:
                    return "텔레메트리 소스: LMU 내장 공유 메모리 (" + LmuNativeLayout.MappingName
                        + ", gameVersion=" + _native.GameVersion + ") — rF2 DLL 불필요"
                        + (gap ? ", 피트 제한속도·페널티 메시지는 rF2 Extended에서 보충" : "");
                case TelemetrySourceKind.Rf2Plugin:
                    return "텔레메트리 소스: rF2 공유 메모리 플러그인 (DLL)"
                        + (_native != null && NativeEnabled ? " — LMU_Data: " + NativeReason() : "");
                default:
                    return "텔레메트리 소스: 없음 — 게임 대기"
                        + (_native != null && NativeEnabled ? " (LMU_Data: " + NativeReason() + ", rF2 DLL 버퍼 없음)" : "");
            }
        }

        private string NativeReason()
        {
            if (_native.GuardFailed)
                return "값 검증 실패 (" + _native.GuardError + ")";
            switch (_native.State)
            {
                case LmuNativeState.Absent:
                    return "없음 (LMU 1.2 이상이면 Settings > Gameplay > Enable Plugins 켜고 게임 재시작)";
                case LmuNativeState.TooSmall:
                    return "크기가 다름 (" + _native.InvalidReason + ")";
                case LmuNativeState.NotRunning:
                    return "gameVersion 0 (게임이 아직 쓰지 않음)";
                case LmuNativeState.Invalid:
                    return "값 이상 (" + _native.InvalidReason + ")";
                case LmuNativeState.Stale:
                    return "5초 넘게 갱신 없음";
                default:
                    return _native.Usable ? "정상" : "검증 대기";
            }
        }

        /// <summary>내장 메모리 상태 변화 로그 — 변화가 있을 때 한 번씩.</summary>
        private void ObserveNative()
        {
            if (_native.Connected != _nativeWasConnected)
            {
                _nativeWasConnected = _native.Connected;
                if (_native.Connected)
                    _info("LMU 내장 공유 메모리(" + LmuNativeLayout.MappingName + ") 갱신 감지 — gameVersion 원시값 "
                          + _native.GameVersion);
                else
                    _info("LMU 내장 공유 메모리 끊김 — " + NativeReason());
            }
            if (_native.GuardFailed && !_nativeWasFailed)
            {
                _warn("LMU 내장 공유 메모리 값 검증 실패 — 형식이 예상과 다릅니다 (게임 업데이트?). "
                      + "이 매핑은 쓰지 않고 rF2 DLL이 있으면 그쪽으로 읽습니다. 사유: " + _native.GuardError
                      + " / gameVersion " + _native.GameVersion);
            }
            _nativeWasFailed = _native.GuardFailed;
            if (_native.Verified && !_nativeWasVerified)
            {
                _info("LMU 내장 공유 메모리 값 검증 통과 (플레이어 mID 일치, 타이어 온도·공기압·마모 정상)");
            }
            _nativeWasVerified = _native.Verified;
            if (_native.State == LmuNativeState.Invalid && _nativeLastState != LmuNativeState.Invalid)
                _info("LMU 내장 공유 메모리 프레임 버림 — " + _native.InvalidReason);
            _nativeLastState = _native.State;
        }

        public void Dispose()
        {
            if (_native != null)
                _native.Dispose();
            _rf2.Dispose();
        }
    }
}
