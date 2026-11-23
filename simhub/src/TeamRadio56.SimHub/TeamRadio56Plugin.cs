using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
// SimHub의 GameReaderCommon에 동명 타입이 있어 이름이 충돌한다
// (예: SharedMemoryReader). 네임스페이스를 통째로 여는 대신 쓰는 타입만
// 별칭으로 고정해 충돌 가능성을 원천 차단한다.
using PluginSettings = TeamRadio56.Core.Config.PluginSettings;
using SettingsStore = TeamRadio56.Core.Config.SettingsStore;
using FileLog = TeamRadio56.Core.Diagnostics.FileLog;
using Rf2SharedMemoryReader = TeamRadio56.Core.Telemetry.Rf2SharedMemoryReader;
using LmuNativeLayout = TeamRadio56.Core.Telemetry.LmuNativeLayout;
using TelemetrySource = TeamRadio56.Core.Telemetry.TelemetrySource;
using TelemetrySourceKind = TeamRadio56.Core.Telemetry.TelemetrySourceKind;
using Snapshot = TeamRadio56.Core.Telemetry.Snapshot;
using RF2Sizes = TeamRadio56.Core.Telemetry.RF2Sizes;
using CrewChiefEngine = TeamRadio56.Core.Logic.CrewChiefEngine;
using EngineSettings = TeamRadio56.Core.Logic.EngineSettings;
using VoiceWorker = TeamRadio56.Core.Logic.VoiceWorker;
using VoiceCache = TeamRadio56.Core.Logic.VoiceCache;
using VoicePolicy = TeamRadio56.Core.Logic.VoicePolicy;
using VoiceRoute = TeamRadio56.Core.Logic.VoiceRoute;
using PregenSentences = TeamRadio56.Core.Logic.PregenSentences;
using PhraseLines = TeamRadio56.Core.Logic.PhraseLines;
using RadioVoice = TeamRadio56.Core.Logic.RadioVoice;
using RecentCall = TeamRadio56.Core.Logic.RecentCall;
using SpeakResult = TeamRadio56.Core.Logic.SpeakResult;
using OverlayState = TeamRadio56.Core.Logic.OverlayState;
using AudioPack = TeamRadio56.Core.Update.AudioPack;
using HttpDownloader = TeamRadio56.Core.Update.HttpDownloader;
using UpdateController = TeamRadio56.Core.Update.UpdateController;
using UpdateService = TeamRadio56.Core.Update.UpdateService;
using UpdateState = TeamRadio56.Core.Update.UpdateState;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// teamradio56 SimHub 플러그인.
    ///
    /// SimHub은 호스팅(자동 실행 / 설정 UI / 배포)을 맡고, 텔레메트리는
    /// 공유 메모리에서 직접 읽는다 — LMU 전용 필드(덴트 존/충격 크기/
    /// pathLateral/횡속도/블루플래그/섹터 플래그)가 SimHub 정규화 계층에
    /// 없기 때문. 그래서 포팅해도 기능 손실이 없다.
    ///
    /// 공유 메모리 원천은 TelemetrySource가 고른다: LMU 1.2+ 내장 메모리(LMU_Data, DLL
    /// 불필요)가 있으면 그것, 없으면 rF2 Shared Memory Map Plugin(DLL) 버퍼 — 예전 그대로.
    ///
    /// 무전은 항상 C# 내장 엔진(Core) + 영어 + kokoro 음성 팩 한 목소리로 나간다.
    /// 음성 팩이 없으면 소리 없이 자막·최근 무전에만 — 다른 목소리로 대신 읽지 않는다.
    /// (파이썬 엔진·온라인 TTS·Windows 음성·한국어 무전은 파이썬 단독 앱에만 있다.)
    /// </summary>
    [PluginDescription("LMU 레이스 엔지니어 음성 무전 — 상황을 판단해 팀라디오로 불러준다")]
    [PluginAuthor("teamradio56")]
    [PluginName("teamradio56")]
    public class TeamRadio56Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string Version = "0.20.1-lite";

        /// <summary>
        /// 플러그인이 무전 엔진을 실제로 돌리는 동안만 쥐는 이름 있는 뮤텍스.
        /// 독립 실행 파이썬 앱(launch_with_lmu.bat 등)이 이걸 보고 물러나 무전이
        /// 두 번 나가지 않는다 — procguard.py의 PLUGIN_MUTEX와 반드시 같은 이름.
        /// 엔진이 안 돌 때도 쥐면 독립 앱까지 물러나 아무도 말하지 않는다.
        /// </summary>
        public const string PluginMutexName = "Local\\teamradio56-plugin";

        private System.Threading.Mutex _pluginMutex;
        private bool _ending;

        private const double PollHz = 5.0;

        // 텔레메트리 원천 — LMU 내장 메모리 우선, 없으면 rF2 DLL 버퍼 (원천이 바뀔 때마다 로그)
        private readonly TelemetrySource _reader = TelemetrySource.CreateDefault(FileLog.Info, FileLog.Warn);

        private CrewChiefEngine _chief;
        private VoiceWorker _voice;
        private RadioVoice _radio;
        private AudioSink _player;
        // 지금 엔진이 쓰는 음성 팩 — InitBuiltin에서만 바뀐다 (설정 화면 표시용)
        private volatile string _packState = VoicePolicy.PackNone;
        private volatile string _installedPack;
        private volatile int _voiceMissing = -1;
        // [테스트 발화] 결과 — 설정 화면 표시용
        private volatile string _testResult;
        // 설치 직후 엔진을 다시 띄워 반영한 음성 팩 (같은 설치로 두 번 재시작하지 않게)
        private string _appliedAudioPack;
        private readonly object _appliedGate = new object();
        // 오버레이 프로퍼티용 실시간 상태 — 공유 메모리에서 직접
        private readonly OverlayState _overlay = new OverlayState();
        private DateTime _nextPoll = DateTime.MinValue;
        private bool _wasConnected;
        private bool _layoutOk;
        private string _loggedGameName;

        public PluginManager PluginManager { get; set; }

        // 시작 직후엔 SimHub·엔진 기동과 겹치지 않게 잠깐 미뤘다가 확인
        private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(15);
        private UpdateController _updater;

        /// <summary>업데이트 확인·설치 (설정 화면이 쓴다). 초기화 실패 시 null.</summary>
        public UpdateController Updater { get { return _updater; } }

        /// <summary>설정 — SimHub 좌측 메뉴에서 편집, DLL 옆 파일에 저장.</summary>
        public PluginSettings Settings { get; private set; }

        public bool IsConnected
        {
            get { return _reader.Connected; }
        }

        /// <summary>지금 쓰는 텔레메트리 원천 (설정 화면 상태 표시용).</summary>
        public TelemetrySourceKind TelemetrySourceActive
        {
            get { return _reader.Active; }
        }

        /// <summary>내장 메모리 + rF2 Extended 보충(피트 제한속도·페널티 메시지) 중인가.</summary>
        public bool TelemetryGapFill
        {
            get { return _reader.GapFill; }
        }

        /// <summary>LMU_Data는 있는데 아직 쓸 수 없음 (메뉴·로딩·일시정지).</summary>
        public bool NativeIdle
        {
            get { return _reader.NativeIdle; }
        }

        /// <summary>내장 메모리를 형식 문제(값 검증 실패·크기·구조)로 쓰지 않는 중인가.</summary>
        public bool NativeRejected
        {
            get { return _reader.NativeRejected; }
        }

        /// <summary>설정 화면 데이터 원천 줄의 Loc 키들 (TelemetrySource.StatusLineKeys).</summary>
        public string[] TelemetryStatusKeys(out bool warn)
        {
            return _reader.StatusLineKeys(out warn);
        }

        /// <summary>독립 실행 앱이 물러나도록 '플러그인 무전 중' 뮤텍스를 쥐거나 놓는다.</summary>
        private void ClaimVoice(bool active)
        {
            try
            {
                if (active && _pluginMutex == null)
                {
                    _pluginMutex = new System.Threading.Mutex(false, PluginMutexName);
                }
                else if (!active && _pluginMutex != null)
                {
                    _pluginMutex.Dispose();
                    _pluginMutex = null;
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("플러그인 무전 뮤텍스 처리 실패 (독립 앱 감지 불가)", ex);
            }
        }

        /// <summary>
        /// 설정을 바꾼 뒤 엔진에 반영 (재시작) — [엔진 재시작] 버튼, 음성 팩 설치 직후.
        /// 이전 엔진을 내리고 같은 설정으로 다시 올린다 (음성 팩 상태도 다시 확인).
        /// </summary>
        private readonly object _restartGate = new object();

        public void RestartEngine()
        {
            // 설정 화면·업데이터가 백그라운드 스레드에서 부른다 — 겹쳐 부르면 순서대로
            lock (_restartGate)
            {
                if (_ending)
                    return;              // SimHub 종료 중 — 새 엔진을 띄우지 않는다
                SaveSettings();
                ShutdownBuiltin();
                ClaimVoice(false);
                InitBuiltin();
                // 재시작을 "새 연결"로 오인하지 않게
                _wasConnected = _reader.Connected;
            }
        }

        /// <summary>
        /// 내장 엔진 정리 — 순서가 중요하다: 새 이벤트 차단 → 재생 중인 소리
        /// 끊기 → 워커가 실제로 멈출 때까지 대기 → 싱크 해제. 순서가 어긋나면
        /// 이전 엔진의 마지막 문장이 새 엔진의 브리핑과 겹친다.
        /// </summary>
        private void ShutdownBuiltin()
        {
            VoiceWorker voice = _voice;
            RadioVoice radio = _radio;
            AudioSink player = _player;
            _chief = null;
            _voice = null;
            _radio = null;
            _player = null;
            _engineInputs = null;
            if (radio != null)
                radio.Stop();
            if (voice != null)
                voice.Dispose();
            if (player != null)
                player.Dispose();
        }

        /// <summary>설정 화면에 보여줄 한 줄 상태.</summary>
        public string StatusText { get; private set; }

        /// <summary>음성 팩(오디오 캐시) 진단 — 설정 화면 엔진 섹션에 표시.</summary>
        public string BuiltinCacheDir { get; private set; }
        public int BuiltinCacheCount { get; private set; }

        // 지금 엔진이 시작할 때 읽은 설정의 지문 (InitBuiltin) — 엔진이 없으면 null
        private volatile string _engineInputs;

        /// <summary>
        /// 엔진이 시작할 때 읽은 뒤로 바뀐 설정이 있나 — [엔진 재시작]을 눌러야 반영된다
        /// (설정 화면이 버튼 옆에 안내한다). 매 틱 다시 읽는 음성 출력만 바꿨으면 false.
        /// 바꿨다가 원래 값으로 되돌려도 false.
        /// </summary>
        public bool EngineSettingsPending
        {
            get
            {
                string applied = _engineInputs;
                PluginSettings settings = Settings;
                return applied != null && settings != null
                    && !string.Equals(applied, EngineInputs(settings), StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// InitBuiltin이 엔진을 만들 때 한 번만 읽는 설정의 지문 (음성 팩 폴더는 그 안의 CreateRadio →
        /// ResolveCacheDir가 읽는다). 음성 출력(VoiceEnabled)은 매 틱 다시 읽어 넣지 않는다.
        /// InitBuiltin이 읽는 설정을 늘리면 여기에도 넣는다 — 빠지면 그 설정을 바꿔도 안내가 안 뜬다
        /// (build-check가 검사). 슬라이더가 같은 칸으로 돌아오면(0.2 간격의 4.6000000000000005 등)
        /// 같은 값으로 보게 소수는 셋째 자리까지.
        /// </summary>
        private static string EngineInputs(PluginSettings s)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0:0.###}|{1}|{2:0.###}|{3:0.###}|{4}|{5}|{6}|{7}|{8}",
                s.CooldownScale, s.RequireRealtime, s.AlongsideMeters, s.StartSpotterSeconds,
                s.SideInvert, s.TrafficRaceOnly, s.LapTimeEveryLap, s.StatusEveryLaps,
                s.AudioCacheDir ?? "");
        }

        // -- IWPFSettingsV2 --------------------------------------------------

        public string LeftMenuTitle { get { return "teamradio56"; } }

        public ImageSource PictureIcon { get { return PluginIcon.Get(); } }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new SettingsControl(this);
        }

        // -- IPlugin ---------------------------------------------------------

        public void Init(PluginManager pluginManager)
        {
            FileLog.Banner(Version);
            // 예전 버전의 키(EngineMode=python, EdgeVoice …)는 무시된다 — 항상 내장 엔진
            Settings = SettingsStore.Load();
            CleanupUpdateLeftovers();
            // 엔진 초기화보다 먼저 — 이후 단계가 실패해도 설정 화면에서 업데이트는 할 수 있게
            InitUpdater();

            string layoutError = Rf2SharedMemoryReader.VerifyLayout();
            _layoutOk = layoutError == null;
            if (_layoutOk)
            {
                FileLog.Info("구조체 레이아웃 검증 통과 (Telemetry {0}B / Scoring {1}B / Extended {2}B)",
                    RF2Sizes.rF2Telemetry, RF2Sizes.rF2Scoring, RF2Sizes.rF2Extended);
            }
            else
            {
                FileLog.Warn("구조체 레이아웃 불일치! 텔레메트리 읽기를 중단한다 — " + layoutError);
            }
            // LMU 내장 메모리(LMU_Data) 상수 자체 검사 — 어긋나면 내장 경로만 끄고 rF2 DLL 경로는 그대로
            string nativeError = LmuNativeLayout.Verify();
            if (nativeError == null)
            {
                FileLog.Info("LMU 내장 공유 메모리 레이아웃 검증 통과 ({0} {1}B, 최대 {2}대)",
                    LmuNativeLayout.MappingName, LmuNativeLayout.TotalSize, LmuNativeLayout.MaxVehicles);
            }
            else
            {
                _reader.NativeEnabled = false;
                FileLog.Warn("LMU 내장 공유 메모리 레이아웃 불일치 — 내장 경로를 끄고 rF2 DLL만 씁니다: "
                             + nativeError);
            }

            StatusText = "대기 중";
            AttachOverlayProperties();

            lock (_restartGate)
                InitBuiltin();
            FileLog.Info("초기화 완료. 로그: {0} / 설정: {1}", FileLog.Path, SettingsStore.Path);
        }

        /// <summary>플러그인 DLL이 있는 폴더 (SimHub 설치 폴더). 알 수 없으면 null.</summary>
        public static string PluginDirectory()
        {
            try
            {
                string location = typeof(TeamRadio56Plugin).Assembly.Location;
                return string.IsNullOrEmpty(location)
                    ? null : System.IO.Path.GetDirectoryName(location);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>지난 업데이트가 남긴 .old DLL·작업 폴더 정리 — 실패는 무시 (다음 시작에 다시).</summary>
        private static void CleanupUpdateLeftovers()
        {
            try
            {
                string dir = PluginDirectory();
                if (dir != null)
                {
                    int removed = UpdateService.CleanupOnStartup(dir);
                    if (removed > 0)
                        FileLog.Info("이전 업데이트의 .old DLL {0}개 정리", removed);
                }
                string temp = UpdateController.TempWorkDir();
                if (System.IO.Directory.Exists(temp))
                    System.IO.Directory.Delete(temp, true);
            }
            catch (Exception ex)
            {
                FileLog.Warn("업데이트 잔여 파일 정리 실패 (무시): " + ex.Message);
            }
        }

        private void InitUpdater()
        {
            try
            {
                string dir = PluginDirectory();
                if (dir == null)
                {
                    FileLog.Warn("플러그인 폴더를 알 수 없어 업데이트 기능을 끕니다");
                    return;
                }
                var service = new UpdateService(new HttpDownloader { UserAgent = "teamradio56/" + Version });
                _updater = new UpdateController(service, Version, dir, UpdateCacheDir);
                _updater.CheckAttempted += at =>
                {
                    PluginSettings s = Settings;
                    if (s == null)
                        return;
                    s.LastUpdateCheck = UpdateService.FormatCheckTime(at);
                    SaveSettings();
                };
                _updater.Changed += OnUpdaterChanged;
                // 음성 팩이 없으면 [음성 팩 업데이트]가 확인 결과를 기다리지 않게 하루 주기와 무관하게 확인
                string cacheDir = UpdateCacheDir();
                bool packMissing = !VoicePolicy.PackUsable(
                    VoicePolicy.KokoroPackState(cacheDir, new VoiceCache(cacheDir)));
                bool due = UpdateService.IsCheckDue(Settings.LastUpdateCheck, DateTime.UtcNow);
                if (Settings.CheckUpdates && (due || packMissing))
                {
                    if (packMissing && !due)
                        FileLog.Info("음성 팩이 없어 시작 시 업데이트를 확인합니다 ([음성 팩 업데이트] 준비)");
                    _updater.StartCheck(StartupCheckDelay);
                }
            }
            catch (Exception ex)
            {
                _updater = null;
                FileLog.Error("업데이트 기능 초기화 실패 (플러그인은 그대로 동작)", ex);
            }
        }

        /// <summary>
        /// 음성 팩 설치가 끝나면 엔진을 다시 띄워 바로 반영한다 — 새 팩 상태를 다시 확인해
        /// 무음 세션이 말하기 시작하고, 설정 화면의 '음성 팩 없음' 안내도 사라진다.
        /// 업데이트 작업 스레드에서 불리므로 재시작은 스레드 풀로 넘긴다.
        /// </summary>
        private void OnUpdaterChanged()
        {
            UpdateController u = _updater;
            UpdateState st = u != null ? u.State : null;
            if (st == null || st.Busy || st.AudioInstalledPack == null)
                return;
            lock (_appliedGate)
            {
                if (st.AudioInstalledPack == _appliedAudioPack)
                    return;
                _appliedAudioPack = st.AudioInstalledPack;
            }
            FileLog.Info("음성 팩 {0} 설치 완료 — 엔진을 다시 시작해 바로 적용", st.AudioInstalledPack);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    RestartEngine();
                }
                catch (Exception ex)
                {
                    FileLog.Error("음성 팩 적용(엔진 재시작) 실패 — [엔진 재시작]을 눌러 주세요", ex);
                }
            });
        }

        /// <summary>음성 팩 설치 대상 — 엔진이 다음 시작 때 쓸 캐시 폴더와 같은 규칙.</summary>
        public string UpdateCacheDir()
        {
            return ResolveCacheDir(false);
        }

        /// <summary>
        /// 음성 팩(오디오 캐시) 폴더 결정 — 엔진과 업데이터가 같은 함수를 써야 업데이터가
        /// 엔진이 찾는 곳에 설치한다. 명시 설정이 최우선, 비어 있거나 없으면 자동 탐색,
        /// 그래도 없으면 DLL 옆 audio_cache ([음성 팩 업데이트]가 만든다).
        /// </summary>
        private string ResolveCacheDir(bool forEngine)
        {
            PluginSettings settings = Settings;
            if (settings != null && !string.IsNullOrEmpty(settings.AudioCacheDir))
            {
                if (System.IO.Directory.Exists(settings.AudioCacheDir))
                    return settings.AudioCacheDir;
                if (forEngine)
                    FileLog.Warn("설정된 오디오 캐시 폴더가 없습니다: "
                                 + settings.AudioCacheDir + " — 자동 탐색으로 폴백");
            }
            string found = AudioSink.FindCacheDir();
            if (found != null)
                return found;
            string dllDir = PluginDirectory();
            return dllDir != null ? System.IO.Path.Combine(dllDir, "audio_cache") : null;
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_layoutOk)
                return;

            // SimHub이 이 게임을 뭐라고 부르는지 한 번만 기록 (다음 단계 참고용)
            if (_loggedGameName == null)
            {
                try
                {
                    string name = data != null ? data.GameName : null;
                    if (!string.IsNullOrEmpty(name))
                    {
                        _loggedGameName = name;
                        FileLog.Info("SimHub 게임 이름: '" + name + "'");
                    }
                }
                catch (Exception)
                {
                    _loggedGameName = "(확인 불가)";
                }
            }

            // SimHub은 60Hz+로 호출한다 — 우리 로직은 5Hz면 충분
            DateTime now = DateTime.UtcNow;
            if (now < _nextPoll)
                return;
            _nextPoll = now.AddSeconds(1.0 / PollHz);

            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                FileLog.Error("틱 처리 실패", ex);
            }
        }

        /// <summary>내장(C#) 엔진 초기화 — 분석기 9종 + 보이스 워커 + kokoro 음성 팩.</summary>
        private void InitBuiltin()
        {
            // 이 엔진이 읽는 설정 — 설정 화면의 "설정이 바뀌었습니다" 안내 기준 (EngineSettingsPending)
            _engineInputs = EngineInputs(Settings);
            var cfg = new EngineSettings
            {
                CooldownScale = Settings.CooldownScale,
                RequireRealtime = Settings.RequireRealtime,
            };
            cfg.Traffic.AlongsideM = Settings.AlongsideMeters;
            cfg.Traffic.StartSpotterSec = Settings.StartSpotterSeconds;
            cfg.Traffic.SideInvert = Settings.SideInvert;
            cfg.Traffic.RaceOnly = Settings.TrafficRaceOnly;

            var chief = new CrewChiefEngine(cfg,
                Settings.LapTimeEveryLap, Settings.StatusEveryLaps);

            string packState;
            AudioSink player;
            _radio = CreateRadio(out player, out packState);
            _player = player;
            _packState = packState;
            _voiceMissing = -1;
            _voice = new VoiceWorker(chief.Bus, _radio);
            _voice.Enabled = Settings.VoiceEnabled;
            _voice.Start();
            ClaimVoice(true);
            _chief = chief;

            VoiceCache cache = _radio.Route.Cache;
            string cacheDir = cache.CacheDir;
            int cacheFiles = 0, segFiles = 0;
            bool exists = cacheDir != null && System.IO.Directory.Exists(cacheDir);
            string installed = null;
            if (exists)
            {
                try
                {
                    cacheFiles = System.IO.Directory
                        .GetFiles(cacheDir, "*_rfx*.wav").Length;
                    segFiles = System.IO.Directory
                        .GetFiles(cacheDir, "*" + VoiceCache.SegSuffix).Length;
                    installed = AudioPack.Installed(cacheDir);
                }
                catch (Exception) { }
            }
            _installedPack = installed;
            BuiltinCacheDir = cacheDir;
            BuiltinCacheCount = cacheFiles;
            FileLog.Info("내장(C#) 엔진 — 분석기 9종, 영어 무전. 음성 팩 폴더: {0}",
                !exists ? (cacheDir ?? "?") + " (없음)"
                        : cacheDir + " (팩 " + (installed ?? "표식 없음") + ", 무전 효과본 " + cacheFiles
                          + "개, 문장 세그먼트 " + segFiles + "개, 조립 자산 "
                          + (cache.SegmentAssetsPresent ? "있음" : "없음") + ")");
            if (VoicePolicy.PackUsable(packState))
                CountMissingLater(_radio);
            else
                FileLog.Warn("음성 팩이 없어 무전을 소리 없이 자막·최근 무전에만 냅니다 — "
                             + "설정 화면 [업데이트] → [음성 팩 업데이트]");
        }

        /// <summary>팩에 없는 멘트 수 — 파일 확인 6천여 번이라 시작을 막지 않게 백그라운드로.</summary>
        private void CountMissingLater(RadioVoice radio)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    int whole, seg;
                    VoicePolicy.CountMissing(radio.Route.Cache, PregenSentences.PackMaxTier,
                        out whole, out seg);
                    if (!ReferenceEquals(radio, _radio))
                        return;          // 그새 엔진이 바뀌었다
                    _voiceMissing = whole + seg;
                    if (whole + seg > 0)
                        FileLog.Warn("음성 팩에 없는 멘트 " + (whole + seg) + "개 (통문장 " + whole
                                     + ", 문장 " + seg + ") — 해당 콜은 자막만");
                }
                catch (Exception ex)
                {
                    FileLog.Error("음성 팩 누락 멘트 확인 실패", ex);
                }
            });
        }

        /// <summary>
        /// kokoro 음성 팩으로 말하는 발화기를 만든다 — 팩 상태는 엔진 시작 때 한 번 정한다.
        /// 팩이 없어도 만든다: 모든 콜이 무음(자막·최근 무전만)으로 기록된다. 쓸 수 없는
        /// 팩(none/incomplete)이면 폴더에 남은 클립도 재생하지 않는다 (VoicePolicy.CreateRoute).
        /// </summary>
        private RadioVoice CreateRadio(out AudioSink player, out string packState)
        {
            string cacheDir = ResolveCacheDir(true);
            VoiceRoute route = VoicePolicy.CreateRoute(cacheDir, out packState);
            string note = VoicePolicy.PackNote(packState);
            FileLog.Info("목소리: kokoro 음성 팩 (상태 " + packState
                         + (note != null ? " — " + note : "")
                         + (route.PackUsable ? "" : ", 이번 세션 무음") + ")");
            player = new AudioSink();
            return new RadioVoice(route, player);
        }

        public void End(PluginManager pluginManager)
        {
            FileLog.Info("종료 — 정리 중");
            // 진행 중인 다운로드는 취소 — DLL 교체 단계는 짧아 끝날 때까지 잠깐 기다린다
            UpdateController updater = _updater;
            if (updater != null)
                updater.Shutdown(TimeSpan.FromSeconds(3));
            // 백그라운드 재시작과 겹치면 End 뒤에 엔진이 새로 뜬다
            lock (_restartGate)
            {
                _ending = true;
                SaveSettings();
                ShutdownBuiltin();
                ClaimVoice(false);
            }
            _reader.Dispose();
        }

        // -- 오버레이 프로퍼티 --------------------------------------------------

        private void UpdateOverlay(Snapshot snap)
        {
            if (!_reader.Connected || snap == null)
            {
                _overlay.Reset();
                return;
            }
            double alongside = Settings != null ? Settings.AlongsideMeters : 4.6;
            bool invert = Settings != null && Settings.SideInvert;
            _overlay.Update(snap, alongside, invert);
        }

        /// <summary>
        /// Dash Studio/LED 등에서 쓸 프로퍼티 노출. 이름은
        /// "TeamRadio56Plugin.이름"으로 보인다.
        /// </summary>
        private void AttachOverlayProperties()
        {
            this.AttachDelegate("Connected", () => IsConnected);
            // 항상 "builtin" — 예전 대시보드 바인딩이 깨지지 않게 남겨 둔다
            this.AttachDelegate("EngineMode", () => "builtin");
            this.AttachDelegate("InSession", () => _overlay.InSession);
            this.AttachDelegate("LastRadio", () => LastRadioText());
            this.AttachDelegate("LastRadioAgeSec", () => LastRadioAgeSec());
            this.AttachDelegate("LastRadioDropped", () => LastRadioDropped());
            this.AttachDelegate("SpotterLeft", () => _overlay.SpotterLeft);
            this.AttachDelegate("SpotterRight", () => _overlay.SpotterRight);
            this.AttachDelegate("NearestAheadM", () => _overlay.NearestAheadM);
            this.AttachDelegate("NearestBehindM", () => _overlay.NearestBehindM);
            this.AttachDelegate("GapAheadSec", () => _overlay.GapAheadSec);
            this.AttachDelegate("GapBehindSec", () => _overlay.GapBehindSec);
            this.AttachDelegate("Position", () => _overlay.Position);
            this.AttachDelegate("ClassPosition", () => _overlay.ClassPosition);
            this.AttachDelegate("HasDamage", () => HasIssue("damage"));
            this.AttachDelegate("HasFuelIssue", () => HasIssue("fuel"));
        }

        private RecentCall LastCall()
        {
            RecentCall[] calls = RecentCallItems();
            return calls != null && calls.Length > 0 ? calls[calls.Length - 1] : null;
        }

        /// <summary>최근 무전 텍스트 (시각 제외).</summary>
        private string LastRadioText()
        {
            RecentCall call = LastCall();
            return call != null ? call.Text : "";
        }

        /// <summary>최근 무전을 소리 없이 자막으로만 냈는가 (음성 팩에 없는 멘트·팩 없음).</summary>
        private bool LastRadioDropped()
        {
            RecentCall call = LastCall();
            return call != null && call.Dropped;
        }

        /// <summary>최근 무전이 나간 뒤 경과 초 (자막 페이드아웃용). 없으면 -1.</summary>
        private double LastRadioAgeSec()
        {
            RecentCall call = LastCall();
            if (call == null || call.Time.Length < 8)
                return -1;
            TimeSpan t;
            if (!TimeSpan.TryParse(call.Time.Substring(0, 8), out t))
                return -1;
            double age = (DateTime.Now.TimeOfDay - t).TotalSeconds;
            if (age < 0)
                age += 86400;      // 자정 넘김
            return Math.Round(age, 1);
        }

        private bool HasIssue(string key)
        {
            CrewChiefEngine chief = _chief;
            return chief != null && chief.State.Issues.ContainsKey(key);
        }

        // -- 설정 화면에서 호출 ------------------------------------------------

        public void SaveSettings()
        {
            if (Settings != null)
                SettingsStore.Save(Settings);
        }

        /// <summary>마지막 [테스트 발화] 결과 한 줄 (설정 화면). 아직 없으면 null.</summary>
        public string TestSpeakResult { get { return _testResult; } }

        public void TestSpeak()
        {
            // 이 줄이 로그에 없으면 버튼/UI 문제, 있는데 결과 줄이
            // 없으면 재생 문제 — 무음 진단의 분기점
            FileLog.Info("[테스트 발화] 버튼 눌림");
            if (Settings == null)
                return;
            RadioVoice radio = _radio;
            string packState = _packState;
            AudioSink transient = null;
            if (radio == null)
            {
                // 엔진 재시작 중 — 엔진과 같은 규칙으로 한 번만 만든다
                try
                {
                    radio = CreateRadio(out transient, out packState);
                }
                catch (Exception ex)
                {
                    FileLog.Error("[테스트 발화] 준비 실패", ex);
                    return;
                }
            }
            // 모든 팩(v015 포함)에 통문장으로 들어 있는 멘트 풀 문구 (pit_call 첫 줄)
            string chosen = VoicePolicy.ProbeText() ?? "Box box, box box.";
            bool usable = VoicePolicy.PackUsable(packState);
            RadioVoice r = radio;
            AudioSink owned = transient;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // 명시적 사용자 동작이라 음성 출력 끔과 무관하게 말한다
                    SpeakResult result = r.Speak(chosen, "casual", false);
                    string detail = result == SpeakResult.Played ? r.LastSource : r.LastReason;
                    _testResult = result == SpeakResult.Played
                        ? Loc.L("test_speak_played")
                        : result == SpeakResult.Dropped
                            ? (usable ? string.Format(Loc.L("test_speak_dropped"), detail ?? "?")
                                      : Loc.L("test_speak_no_pack"))
                            : null;
                    FileLog.Info("[테스트 발화] " + result + " (" + (detail ?? "?") + "): " + chosen);
                }
                catch (Exception ex)
                {
                    FileLog.Error("[테스트 발화] 실패", ex);
                }
                finally
                {
                    if (owned != null)
                        owned.Dispose();
                }
            });
        }

        /// <summary>최근 무전 (음성 팩에 없어 자막으로만 낸 콜 표시 포함).</summary>
        public RecentCall[] RecentCallItems()
        {
            VoiceWorker voice = _voice;
            return voice != null ? voice.RecentCallItems() : new RecentCall[0];
        }

        /// <summary>설정 화면 상태 블록용 — 음성 팩 상태와 무음 건수.</summary>
        public sealed class VoiceInfo
        {
            /// <summary>ok | partial | incomplete | none (VoicePolicy.Pack*).</summary>
            public string PackState;
            /// <summary>no-kokoro | pack-incomplete | pack-partial, ok면 null.</summary>
            public string Note;
            /// <summary>설치된 팩 ID (표식/옛 v015 휴리스틱, 없으면 null).</summary>
            public string InstalledPack;
            public bool PackUsable;
            public int Dropped;
            public int Missing = -1;
        }

        /// <summary>지금 엔진의 음성 팩 정보. 엔진이 없으면(재시작 중) null.</summary>
        public VoiceInfo CurrentVoiceInfo()
        {
            RadioVoice radio = _radio;
            if (radio == null)
                return null;
            string state = _packState;
            return new VoiceInfo
            {
                PackState = state,
                Note = VoicePolicy.PackNote(state),
                InstalledPack = _installedPack,
                PackUsable = VoicePolicy.PackUsable(state),
                Dropped = radio.Drops.Count,
                Missing = _voiceMissing,
            };
        }

        // -- 내부 ------------------------------------------------------------

        private void Tick()
        {
            Snapshot snap = _reader.Poll();
            UpdateOverlay(snap);

            if (_reader.Connected != _wasConnected)
            {
                _wasConnected = _reader.Connected;
                FileLog.Info(_reader.Connected
                    ? "LMU 공유 메모리 연결됨"
                    : "LMU 공유 메모리 끊김 (게임 종료/일시정지)");
                // 연결 확인음은 브리핑이 대신한다 — 여기서 따로 말하면 워커
                // 밖 재생이라 브리핑/스포터 콜과 겹쳤다
            }

            // RestartEngine(백그라운드 스레드)이 도중에 갈아끼울 수 있어 로컬로 잡는다
            CrewChiefEngine chief = _chief;
            VoiceWorker voice = _voice;
            if (chief == null)
                return;
            if (voice != null && Settings != null)
            {
                voice.Enabled = Settings.VoiceEnabled;
            }
            chief.OnPoll(snap, _reader.Connected);
            StatusText = chief.StatusText;
        }
    }
}
