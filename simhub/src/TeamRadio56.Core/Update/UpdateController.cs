using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Update
{
    public enum UpdateActivity { Idle, Checking, InstallingPlugin, InstallingAudio }

    /// <summary>업데이트 화면 상태 스냅샷 (불변) — UI는 이걸 읽어 Loc 문구로 그린다.</summary>
    public sealed class UpdateState
    {
        public UpdateActivity Activity { get; private set; }
        /// <summary>마지막으로 성공한 확인 결과 (없으면 null).</summary>
        public UpdateCheck Check { get; private set; }
        public DateTime? CheckedAtUtc { get; private set; }
        /// <summary>진행 중 작업의 최근 진행률 (없으면 null).</summary>
        public UpdateProgress Progress { get; private set; }
        /// <summary>마지막 작업이 실패했으면 그 작업과 분류. 성공·새 작업 시작 시 지워진다.</summary>
        public UpdateActivity FailedActivity { get; private set; }
        public UpdateErrorKind? ErrorKind { get; private set; }
        public string ErrorDetail { get; private set; }
        /// <summary>플러그인 DLL을 교체했고 SimHub 재시작을 기다리는 중 — 설치된 버전.</summary>
        public string RestartPendingVersion { get; private set; }
        /// <summary>이번 세션에 설치한 음성 팩 ID (엔진 재시작 후 적용).</summary>
        public string AudioInstalledPack { get; private set; }
        /// <summary>
        /// DLL 교체의 되돌리기가 실패해 사용자가 직접 바꿔야 할 (백업, 원래 이름) 쌍 — 없으면 null.
        /// 새 작업이 시작돼도 지우지 않는다 (재시작 전에 꼭 봐야 하는 안내).
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, string>> ManualRestore { get; private set; }

        public bool Busy => Activity != UpdateActivity.Idle;
        public bool RestartPending => RestartPendingVersion != null;
        public bool ManualRestorePending => ManualRestore != null;

        /// <summary>
        /// 취소 가능 여부 — 풀기·교체 단계에선 작업이 취소를 무시하므로 버튼도 숨긴다.
        /// </summary>
        public bool CanCancel =>
            Busy && (Progress == null || Progress.Stage == UpdateStage.Downloading
                     || Progress.Stage == UpdateStage.Verifying);

        // 수동 복구 대기 중 재설치하면 새 DLL이 다시 백업돼 복구할 원래 파일 안내가 흐려진다
        public bool CanInstallPlugin =>
            !Busy && !RestartPending && !ManualRestorePending && Check != null && Check.PluginUpdateAvailable;

        public bool CanInstallAudio =>
            !Busy && Check != null && Check.AudioUpdateAvailable;

        internal UpdateState With(Action<UpdateState> change)
        {
            var copy = (UpdateState)MemberwiseClone();
            change(copy);
            return copy;
        }

        internal static readonly UpdateState Initial = new UpdateState();

        internal void SetActivity(UpdateActivity a) => Activity = a;
        internal void SetCheck(UpdateCheck c, DateTime? at) { Check = c; if (at.HasValue) CheckedAtUtc = at; }
        internal void SetProgress(UpdateProgress p) => Progress = p;
        internal void SetError(UpdateActivity failed, UpdateErrorKind? kind, string detail)
        {
            FailedActivity = failed;
            ErrorKind = kind;
            ErrorDetail = detail;
        }
        internal void SetRestartPending(string version) => RestartPendingVersion = version;
        internal void SetAudioInstalled(string pack) => AudioInstalledPack = pack;
        internal void SetManualRestore(IReadOnlyList<KeyValuePair<string, string>> restore) =>
            ManualRestore = restore;
    }

    /// <summary>
    /// 플러그인 쪽 업데이트 흐름: 한 번에 작업 하나, 전부 백그라운드 스레드에서.
    /// 예외는 밖으로 내보내지 않고 상태(ErrorKind)로만 남긴다 — SimHub을 흔들지 않게.
    /// 설치는 호출부(사용자 버튼)가 부를 때만 한다.
    /// </summary>
    public sealed class UpdateController
    {
        // 진행률 알림 최소 간격 — 64KB마다 오는 콜백을 그대로 UI로 넘기지 않는다
        private static readonly TimeSpan ProgressNotifyInterval = TimeSpan.FromMilliseconds(250);

        private readonly UpdateService _service;
        private readonly string _currentVersion;
        private readonly string _pluginDir;
        private readonly Func<string> _cacheDir;
        private readonly object _gate = new object();

        private volatile UpdateState _state = UpdateState.Initial;
        private CancellationTokenSource _cts;
        private Thread _worker;
        private DateTime _lastProgressNotify = DateTime.MinValue;
        private UpdateStage? _lastProgressStage;
        private bool _shutdown;

        /// <summary>상태가 바뀌면 (작업 스레드에서) 불린다.</summary>
        public event Action Changed;

        /// <summary>확인이 끝나면 (성공·실패, 취소 제외) 시각과 함께 불린다 — 마지막 확인 시각 저장용.</summary>
        public event Action<DateTime> CheckAttempted;

        /// <param name="cacheDir">음성 팩을 설치할 오디오 캐시 폴더 — 작업 때마다 다시 구한다.</param>
        public UpdateController(UpdateService service, string currentVersion, string pluginDir,
                                Func<string> cacheDir)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _currentVersion = currentVersion;
            _pluginDir = pluginDir;
            _cacheDir = cacheDir ?? (() => null);
        }

        public UpdateState State => _state;
        public string CurrentVersion => _currentVersion;
        public string PluginDir => _pluginDir;

        /// <summary>확인 시작. delay 동안 기다렸다가 (시작 직후 SimHub 부하 회피) 받는다.</summary>
        public bool StartCheck(TimeSpan delay = default(TimeSpan))
        {
            return Start(UpdateActivity.Checking, ct =>
            {
                if (delay > TimeSpan.Zero && ct.WaitHandle.WaitOne(delay))
                    ct.ThrowIfCancellationRequested();
                try
                {
                    UpdateCheck check = _service.Check(_currentVersion, SafeCacheDir(), ct);
                    Update(s => s.SetCheck(check, DateTime.UtcNow));
                    FileLog.Info("업데이트 확인: 최신 {0} (현재 {1}) — 플러그인 {2}, 음성 팩 {3} (설치됨: {4})",
                        check.Latest.Version, _currentVersion,
                        check.PluginUpdateAvailable ? "업데이트 있음" : "최신",
                        check.Latest.HasAudioPack ? check.Latest.AudioPack : "-",
                        check.InstalledAudioPack ?? "없음");
                }
                finally
                {
                    if (!ct.IsCancellationRequested)
                        RaiseCheckAttempted(DateTime.UtcNow);
                }
            });
        }

        public bool StartInstallPlugin()
        {
            UpdateState s0 = _state;
            if (!s0.CanInstallPlugin)
                return false;
            ReleaseManifest m = s0.Check.Latest;
            return Start(UpdateActivity.InstallingPlugin, ct =>
            {
                FileLog.Info("플러그인 업데이트 시작: {0} → {1}", _currentVersion, m.Version);
                _service.InstallPlugin(m, _pluginDir, WorkDir(), OnProgress, ct);
                Update(s => s.SetRestartPending(m.Version));
                FileLog.Info("플러그인 업데이트 완료 ({0}) — SimHub 재시작 후 적용", m.Version);
            });
        }

        public bool StartInstallAudio()
        {
            UpdateState s0 = _state;
            if (!s0.CanInstallAudio)
                return false;
            ReleaseManifest m = s0.Check.Latest;
            return Start(UpdateActivity.InstallingAudio, ct =>
            {
                string cache = SafeCacheDir();
                if (string.IsNullOrEmpty(cache))
                    throw new UpdateException(UpdateErrorKind.Io, "오디오 캐시 폴더를 정할 수 없음");
                FileLog.Info("음성 팩 업데이트 시작: {0} → {1}", cache, m.AudioPack);
                try
                {
                    _service.InstallAudioPack(m, cache, WorkDir(), OnProgress, ct);
                }
                catch (Exception)
                {
                    // 풀기 도중 실패면 표식이 '미완료'로 바뀌었다 — 화면이 이전 팩을 계속 보여주지 않게
                    RefreshInstalledPack(m, cache, null);
                    throw;
                }
                RefreshInstalledPack(m, cache, m.AudioPack);
                FileLog.Info("음성 팩 설치 완료: {0} ({1})", m.AudioPack, cache);
            });
        }

        private void RefreshInstalledPack(ReleaseManifest m, string cache, string justInstalled)
        {
            string installed = null;
            try
            {
                installed = AudioPack.Installed(cache);
            }
            catch (Exception) { }
            Update(s =>
            {
                if (justInstalled != null)
                    s.SetAudioInstalled(justInstalled);
                s.SetCheck(new UpdateCheck(m, _currentVersion, installed), null);
            });
        }

        public void Cancel()
        {
            lock (_gate)
            {
                try
                {
                    _cts?.Cancel();
                }
                catch (ObjectDisposedException) { }
            }
        }

        /// <summary>작업이 끝날 때까지 기다린다 (최대 timeout). 끝났으면 true.</summary>
        public bool Wait(TimeSpan timeout)
        {
            Thread t;
            lock (_gate)
                t = _worker;
            return t == null || t.Join(timeout);
        }

        /// <summary>플러그인 End: 진행 중 작업 취소, 잠깐 기다린다. 이후 새 작업은 거부.</summary>
        public void Shutdown(TimeSpan wait)
        {
            lock (_gate)
                _shutdown = true;
            Cancel();
            Wait(wait);
        }

        /// <summary>
        /// 작업 폴더: 플러그인 폴더 아래가 기본, 쓸 수 없으면 %TEMP% — 음성 팩은 플러그인
        /// 폴더 권한과 무관하게 받을 수 있어야 한다.
        /// </summary>
        public string WorkDir()
        {
            string preferred = UpdateService.DefaultWorkDir(_pluginDir);
            try
            {
                Directory.CreateDirectory(preferred);
                string probe = Path.Combine(preferred, "teamradio56_write_probe.tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return preferred;
            }
            catch (Exception ex)
            {
                string temp = TempWorkDir();
                FileLog.Warn("플러그인 폴더에 작업 폴더를 만들 수 없음 (" + ex.Message + ") — " + temp + " 사용");
                return temp;
            }
        }

        public static string TempWorkDir() =>
            Path.Combine(Path.GetTempPath(), UpdateService.WorkDirName);

        // -- 내부 ------------------------------------------------------------

        private bool Start(UpdateActivity activity, Action<CancellationToken> work)
        {
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (_shutdown || _state.Busy)
                    return false;
                cts = new CancellationTokenSource();
                _cts = cts;
                _lastProgressStage = null;
                _state = _state.With(s =>
                {
                    s.SetActivity(activity);
                    s.SetProgress(null);
                    s.SetError(UpdateActivity.Idle, null, null);
                });
                _worker = new Thread(() => Run(activity, work, cts))
                {
                    IsBackground = true,
                    Name = "teamradio56-update",
                };
                _worker.Start();
            }
            RaiseChanged();
            return true;
        }

        private void Run(UpdateActivity activity, Action<CancellationToken> work,
                         CancellationTokenSource cts)
        {
            UpdateException failure = null;
            try
            {
                work(cts.Token);
            }
            catch (Exception ex)
            {
                failure = UpdateException.From(ex, activity.ToString());
                if (failure.Kind == UpdateErrorKind.Cancelled)
                    FileLog.Info("업데이트 작업 취소됨 (" + activity + ")");
                else
                    FileLog.Error("업데이트 작업 실패 (" + activity + ", " + failure.Kind + ")", ex);
            }
            lock (_gate)
            {
                _state = _state.With(s =>
                {
                    s.SetActivity(UpdateActivity.Idle);
                    s.SetProgress(null);
                    if (failure != null)
                        s.SetError(activity, failure.Kind, failure.Message);
                    if (failure != null && failure.Kind == UpdateErrorKind.RollbackFailed)
                        s.SetManualRestore(failure.ManualRestore);
                });
                if (ReferenceEquals(_cts, cts))
                    _cts = null;
            }
            cts.Dispose();
            RaiseChanged();
        }

        private void OnProgress(UpdateProgress p)
        {
            DateTime now = DateTime.UtcNow;
            bool stageChanged = _lastProgressStage != p.Stage;
            Update(s => s.SetProgress(p), notify: false);
            if (stageChanged || now - _lastProgressNotify >= ProgressNotifyInterval)
            {
                _lastProgressStage = p.Stage;
                _lastProgressNotify = now;
                RaiseChanged();
            }
        }

        private void Update(Action<UpdateState> change, bool notify = true)
        {
            lock (_gate)
                _state = _state.With(change);
            if (notify)
                RaiseChanged();
        }

        private string SafeCacheDir()
        {
            try
            {
                return _cacheDir();
            }
            catch (Exception ex)
            {
                FileLog.Error("오디오 캐시 폴더 확인 실패", ex);
                return null;
            }
        }

        private void RaiseChanged()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                FileLog.Error("업데이트 상태 알림 처리 실패", ex);
            }
        }

        private void RaiseCheckAttempted(DateTime at)
        {
            try
            {
                CheckAttempted?.Invoke(at);
            }
            catch (Exception ex)
            {
                FileLog.Error("업데이트 확인 시각 기록 실패", ex);
            }
        }
    }
}
