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
    /// rF2 버퍼는 내장 메모리를 쓰는 동안에도 매 틱 버전만 따라간다 (IVersionTracker, 8바이트).
    /// 그래야 일시정지·게임 종료로 내장 메모리가 멈췄을 때 오래 안 읽은 rF2 버퍼가 "방금 갱신됨"
    /// 으로 보여 멈춘 게임을 5초 더 "연결"로 내는 일이 없다. 게다가 내장 메모리가 멈춰서(Stale·
    /// NotRunning·Absent) 내려놓은 경우에는 rF2 버전이 그 뒤에 한 번 더 바뀌어야 넘어간다 —
    /// 같은 게임이 멈춘 것이므로 보통은 "게임 대기"로 간다. 값 검증 실패처럼 게임은 도는데 내장
    /// 데이터만 못 쓰는 경우에는 rF2 자체 연결 판정대로 바로 넘어간다.
    ///
    /// 진단: 내장 메모리를 쓰는 중에 rF2 DLL 버퍼도 있으면, 값 검증을 통과하고 5초쯤 뒤 한 번
    /// rF2 버퍼도 읽어 두 스냅샷을 대조한 결과를 로그에 남긴다 (SnapshotCompare — 엔진에는 넘기지
    /// 않는다). 매핑을 열 때마다(게임 실행마다) 한 번 — 일시정지 뒤에는 다시 하지 않는다.
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

        private bool _nativeWasRejected;
        private bool _nativeWasSlotMissing;
        private bool _guardWarned;

        // 내장 메모리가 멈춰서 내려놓은 뒤에는 rF2 버전이 이 값보다 많이 바뀌어야 넘어간다
        private bool _rf2NeedsFresh;
        private long _rf2ChangesAtDrop;

        /// <summary>내장 메모리 사용 몇 틱 뒤 rF2 버퍼와 한 번 대조할지 (5Hz 기준 약 5초).</summary>
        public const int CrossCheckAfterPolls = 25;
        private int _crossCheckPolls;
        private int _crossCheckedOpen;

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
            // 보충은 DLL이 이번 실행에서 살아 있을 때만 (멈춘·지난 실행의 Extended를 쓰지 않는다)
            var native = new LmuNativeReader(
                new MappedLmuMemory(LmuNativeLayout.MappingName), null, rf2.TryReadLiveExtended);
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

        /// <summary>
        /// 내장 메모리를 형식 문제로 쓰지 않는 중: 값 검증 실패, 매핑이 기대보다 작음, 또는
        /// 프레임이 계속 구조 검사에 걸림 (게임 업데이트로 형식이 바뀐 듯).
        /// </summary>
        public bool NativeRejected
        {
            get
            {
                return _native != null && NativeEnabled
                    && (_native.GuardFailed || _native.State == LmuNativeState.TooSmall
                        || _native.StructurallyRejected);
            }
        }

        /// <summary>
        /// LMU_Data 매핑이 지금 있다 (열렸다) — Enable Plugins는 이미 켜진 것. 멈춘 매핑은 리더가
        /// 주기적으로 닫았다 다시 열어 보므로, 게임이 꺼지면 곧 false가 된다.
        /// </summary>
        public bool NativeMappingPresent
        {
            get
            {
                return _native != null && NativeEnabled
                    && (_native.State != LmuNativeState.Absent || _native.GuardFailed);
            }
        }

        public LmuNativeReader Native { get { return _native; } }

        /// <summary>
        /// LMU_Data 매핑은 있는데 아직/더 이상 쓸 수 없다 (메뉴·로딩·일시정지·잠깐 이상한 프레임) —
        /// 설정 화면이 "Enable Plugins를 켜라" 대신 "세션에 들어가면 연결" 안내를 고르는 데 쓴다.
        /// 형식 문제(NativeRejected)는 제외.
        /// </summary>
        public bool NativeIdle
        {
            get { return NativeMappingPresent && !NativeRejected && !_native.Usable; }
        }

        /// <summary>
        /// 설정 화면 데이터 원천 줄의 문자열 키 (Loc) — 위에서부터 한 줄씩. warn이면 경고색.
        ///  - 내장: src_native / src_native_gapfill
        ///  - rF2: src_rf2 (+ 내장을 형식 문제로 거부했으면 src_native_rejected)
        ///  - 없음: 형식 문제면 src_native_rejected + src_native_rejected_nodll (Enable Plugins 안내는
        ///    하지 않는다 — 매핑을 읽었으니 이미 켜져 있다), 매핑이 있으면 src_native_idle,
        ///    매핑도 없으면 src_waiting.
        /// </summary>
        public string[] StatusLineKeys(out bool warn)
        {
            warn = false;
            switch (Active)
            {
                case TelemetrySourceKind.LmuNative:
                    return new[] { GapFill ? "src_native_gapfill" : "src_native" };
                case TelemetrySourceKind.Rf2Plugin:
                    if (NativeRejected)
                    {
                        warn = true;
                        return new[] { "src_rf2", "src_native_rejected" };
                    }
                    return new[] { "src_rf2" };
                default:
                    if (NativeRejected)
                    {
                        warn = true;
                        return new[] { "src_native_rejected", "src_native_rejected_nodll" };
                    }
                    return new[] { NativeMappingPresent ? "src_native_idle" : "src_waiting" };
            }
        }

        public Snapshot Poll()
        {
            Snapshot snap = null;
            TelemetrySourceKind next = TelemetrySourceKind.None;
            var tracker = _rf2 as IVersionTracker;

            if (_native != null && NativeEnabled)
            {
                // 내장을 쓰는 동안에도 rF2 버전을 따라간다 — 넘어갈 때 멈춘 버퍼를 새것으로 착각하지 않게
                if (tracker != null)
                    tracker.TrackVersion();
                Snapshot ns = _native.Poll();
                ObserveNative();
                if (_native.Usable)
                {
                    next = TelemetrySourceKind.LmuNative;
                    snap = ns;
                    _rf2NeedsFresh = false;
                }
                else if (Active == TelemetrySourceKind.LmuNative && tracker != null && NativeStopped())
                {
                    // 게임이 멈춰서 내려놓음 — rF2도 같은 게임이니 그 뒤에 실제로 움직여야 넘어간다
                    _rf2NeedsFresh = true;
                    _rf2ChangesAtDrop = tracker.VersionChanges;
                }
            }
            if (next == TelemetrySourceKind.None)
            {
                Snapshot rs = _rf2.Poll();
                bool fresh = !_rf2NeedsFresh || tracker == null || tracker.VersionChanges > _rf2ChangesAtDrop;
                if (_rf2.Connected && fresh)
                {
                    next = TelemetrySourceKind.Rf2Plugin;
                    snap = rs;
                    _rf2NeedsFresh = false;
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

            // 교차 확인은 매핑을 열 때마다 한 번 (일시정지·잠깐의 원천 전환으로 다시 하지 않는다)
            if (next != TelemetrySourceKind.LmuNative)
            {
                _crossCheckPolls = 0;
            }
            else if (_crossCheckedOpen != _native.OpenCount && snap != null && _native.Verified
                     && ++_crossCheckPolls >= CrossCheckAfterPolls)
            {
                _crossCheckedOpen = _native.OpenCount;
                _crossCheckPolls = 0;
                CrossCheck(snap);
            }
            return snap;
        }

        /// <summary>내장 메모리가 게임이 멈춰서(데이터가 나빠서가 아니라) 못 쓰게 됐나.</summary>
        private bool NativeStopped()
        {
            return !_native.GuardFailed
                && (_native.State == LmuNativeState.Stale || _native.State == LmuNativeState.NotRunning
                    || _native.State == LmuNativeState.Absent);
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
            if (_native.StructurallyRejected)
                return "프레임이 계속 구조 검사에 걸림 — 형식이 바뀐 듯 (" + _native.InvalidReason + ")";
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
                case LmuNativeState.Torn:
                    return "복사 중 계속 바뀜 (찢긴 읽기 " + _native.TornFrames + "회)";
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
                // 멈췄다 움직이면 리더가 실패를 풀고 다시 판정한다 — 진짜 형식 문제면 또 실패하므로 경고는 한 번
                if (!_guardWarned)
                    _warn("LMU 내장 공유 메모리 값 검증 실패 — 형식이 예상과 다릅니다 (게임 업데이트?). "
                          + "이 매핑은 쓰지 않고 rF2 DLL이 있으면 그쪽으로 읽습니다. 사유: " + _native.GuardError
                          + " / gameVersion " + _native.GameVersion);
                else
                    _info("LMU 내장 공유 메모리 값 검증 다시 실패 — " + _native.GuardError);
                _guardWarned = true;
            }
            _nativeWasFailed = _native.GuardFailed;
            if (_native.StructurallyRejected && !_nativeWasRejected)
            {
                _warn("LMU 내장 공유 메모리 프레임이 " + LmuNativeReader.InvalidFramesToReject
                      + "번 연속 구조 검사에 걸림 — 형식이 예상과 다릅니다 (게임 업데이트?). 사유: "
                      + _native.InvalidReason + " / gameVersion " + _native.GameVersion);
            }
            _nativeWasRejected = _native.StructurallyRejected;
            if (_native.PlayerSlotMissing && !_nativeWasSlotMissing)
            {
                _info("LMU 내장 공유 메모리: 달리는 중인데 플레이어 텔레메트리 슬롯이 없음 (activeVehicles "
                      + _native.ActiveVehicles + ") — 플레이어 정보 없이 진행 (형식 실패로 보지 않음)");
            }
            _nativeWasSlotMissing = _native.PlayerSlotMissing;
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
