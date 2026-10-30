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
using EngineHost = TeamRadio56.Core.Engine.EngineHost;
using EngineStatus = TeamRadio56.Core.Engine.EngineStatus;
using Rf2SharedMemoryReader = TeamRadio56.Core.Telemetry.Rf2SharedMemoryReader;
using Snapshot = TeamRadio56.Core.Telemetry.Snapshot;
using RF2Sizes = TeamRadio56.Core.Telemetry.RF2Sizes;
using CrewChiefEngine = TeamRadio56.Core.Logic.CrewChiefEngine;
using EngineSettings = TeamRadio56.Core.Logic.EngineSettings;
using VoiceWorker = TeamRadio56.Core.Logic.VoiceWorker;
using VoiceCache = TeamRadio56.Core.Logic.VoiceCache;
using VoiceKind = TeamRadio56.Core.Logic.VoiceKind;
using VoicePolicy = TeamRadio56.Core.Logic.VoicePolicy;
using PregenSentences = TeamRadio56.Core.Logic.PregenSentences;
using PhraseLines = TeamRadio56.Core.Logic.PhraseLines;
using VoiceRoute = TeamRadio56.Core.Logic.VoiceRoute;
using RadioVoice = TeamRadio56.Core.Logic.RadioVoice;
using RecentCall = TeamRadio56.Core.Logic.RecentCall;
using SpeakResult = TeamRadio56.Core.Logic.SpeakResult;
using OverlayState = TeamRadio56.Core.Logic.OverlayState;
using HttpDownloader = TeamRadio56.Core.Update.HttpDownloader;
using UpdateController = TeamRadio56.Core.Update.UpdateController;
using UpdateService = TeamRadio56.Core.Update.UpdateService;

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
    /// 현재 단계: 설정 UI + 텔레메트리 읽기까지. 분석기/멘트 풀/무전 효과는
    /// 다음 단계에서 Core에 이식한다.
    /// </summary>
    [PluginDescription("LMU 레이스 엔지니어 음성 무전 — 상황을 판단해 팀라디오로 불러준다")]
    [PluginAuthor("teamradio56")]
    [PluginName("teamradio56")]
    public class TeamRadio56Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string Version = "0.16.1-raceend";

        /// <summary>
        /// 플러그인이 무전 엔진(builtin 또는 자식 파이썬)을 실제로 돌리는 동안만
        /// 쥐는 이름 있는 뮤텍스. 독립 실행 파이썬 엔진(launch_with_lmu.bat 등)이
        /// 이걸 보고 물러난다 — procguard.py의 PLUGIN_MUTEX와 반드시 같은 이름.
        /// 엔진이 안 돌 때도 쥐면 독립 엔진까지 물러나 아무도 말하지 않는다.
        /// </summary>
        public const string PluginMutexName = "Local\\teamradio56-plugin";

        private System.Threading.Mutex _pluginMutex;
        private bool _ending;

        private const double PollHz = 5.0;

        private readonly Rf2SharedMemoryReader _reader = new Rf2SharedMemoryReader();

        private readonly EngineHost _engine = new EngineHost();
        private readonly EngineStatus _engineStatus = new EngineStatus();
        private SpeechOutput _speech;        // 목소리 섞임 허용(en)일 때만 만든다
        private readonly object _speechGate = new object();
        private CrewChiefEngine _chief;      // 내장(C#) 엔진 모드
        private VoiceWorker _voice;
        private RadioVoice _radio;
        private AudioSink _player;
        private string _voiceNote;
        private volatile int _voiceMissing = -1;
        // [테스트 발화] 결과 — 설정 화면 표시용
        private volatile string _testResult;
        // 오버레이 프로퍼티용 실시간 상태 — 엔진 모드와 무관하게 공유 메모리에서
        private readonly OverlayState _overlay = new OverlayState();
        private DateTime _nextStatusRead = DateTime.MinValue;
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
            get { return UsingPythonEngine ? _engineStatus.GetBool("connected") : _reader.Connected; }
        }

        // 실제로 "돌고 있는" 모드 — 설정값이 아니라 Init/RestartEngine에서만
        // 갱신된다. 설정 콤보만 바꾸고 아직 전환 전인 어정쩡한 상태에서
        // Tick이 반대 분기로 새는 것을 막는다.
        private string _activeMode;

        /// <summary>파이썬 엔진 모드로 동작 중인가 (C# 이식 완료 전 기본값).</summary>
        public bool UsingPythonEngine
        {
            get
            {
                string mode = _activeMode
                    ?? (Settings != null ? Settings.EngineMode : null);
                return !string.Equals(mode, "builtin",
                                      StringComparison.OrdinalIgnoreCase);
            }
        }

        public bool EngineRunning { get { return _engine.IsRunning; } }
        public string EngineError { get { return _engine.LastError; } }
        public string EngineLogPath { get { return EngineHost.EngineLogPath(); } }

        public string EngineExePath()
        {
            string configured = Settings != null ? Settings.EngineExe : null;
            return string.IsNullOrEmpty(configured) ? EngineHost.DefaultExePath() : configured;
        }

        /// <summary>설정 화면의 [엔진 시작] — 설정을 먼저 저장해 엔진이 최신 값을 읽게 한다.</summary>
        public bool StartEngine()
        {
            lock (_restartGate)
            {
                if (_ending)
                    return false;
                SaveSettings();
                bool ok = _engine.Start(EngineExePath(),
                                        Settings != null ? Settings.EngineArgs : null,
                                        SettingsStore.Path, EngineStatus.DefaultPath());
                ClaimVoice(ok);
                return ok;
            }
        }

        public void StopEngine()
        {
            lock (_restartGate)
            {
                _engine.Stop();
                ClaimVoice(false);
            }
        }

        /// <summary>독립 실행 엔진이 물러나도록 '플러그인 무전 중' 뮤텍스를 쥐거나 놓는다.</summary>
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
                FileLog.Error("플러그인 무전 뮤텍스 처리 실패 (독립 엔진 감지 불가)", ex);
            }
        }

        /// <summary>
        /// 설정을 바꾼 뒤 엔진에 반영 (재시작). 모드 전환(python↔builtin)도
        /// 여기서 처리한다 — 이전 모드를 내리고 현재 모드로 다시 올린다.
        /// </summary>
        private readonly object _restartGate = new object();

        public void RestartEngine()
        {
            // 설정 화면이 백그라운드 스레드에서 부른다 — 겹쳐 부르면 순서대로
            lock (_restartGate)
            {
                if (_ending)
                    return;              // SimHub 종료 중 — 새 엔진을 띄우지 않는다
                SaveSettings();
                _engine.Stop();          // 파이썬 엔진이 돌고 있으면 종료
                ShutdownBuiltin();       // 내장 엔진이 돌고 있으면 정리
                ClaimVoice(false);
                _activeMode = Settings != null ? Settings.EngineMode : null;
                if (UsingPythonEngine)
                    StartEngine();
                else
                    InitBuiltin();
                // 모드 전환을 "새 연결"로 오인하지 않게
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
            if (radio != null)
                radio.Stop();
            if (voice != null)
                voice.Dispose();
            if (player != null)
                player.Dispose();
        }

        /// <summary>설정 화면에 보여줄 한 줄 상태.</summary>
        public string StatusText { get; private set; }

        /// <summary>builtin 오디오 캐시 진단 — 설정 화면 엔진 섹션에 표시.</summary>
        public string BuiltinCacheDir { get; private set; }
        public int BuiltinCacheCount { get; private set; }

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
            Settings = SettingsStore.Load();
            _activeMode = Settings.EngineMode;
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

            StatusText = "대기 중";
            AttachOverlayProperties();

            if (UsingPythonEngine)
            {
                if (StartEngine())
                    FileLog.Info("파이썬 엔진 모드 — 전체 기능 동작");
                else
                    FileLog.Warn("엔진을 띄우지 못했습니다: " + (_engine.LastError ?? "원인 불명"));
            }
            else
            {
                InitBuiltin();
            }
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
                if (Settings.CheckUpdates
                    && UpdateService.IsCheckDue(Settings.LastUpdateCheck, DateTime.UtcNow))
                    _updater.StartCheck(StartupCheckDelay);
            }
            catch (Exception ex)
            {
                _updater = null;
                FileLog.Error("업데이트 기능 초기화 실패 (플러그인은 그대로 동작)", ex);
            }
        }

        /// <summary>음성 팩 설치 대상 — 엔진이 다음 시작 때 쓸 캐시 폴더와 같은 규칙.</summary>
        public string UpdateCacheDir()
        {
            return ResolveCacheDir(false);
        }

        /// <summary>
        /// 오디오 캐시 폴더 결정. 명시 설정이 최우선, 비어 있으면 자동 탐색 (파이썬
        /// 설정 잔여값은 마지막 폴백일 뿐 — builtin은 원칙적으로 여기에 기대지 않는다),
        /// 그래도 없으면 DLL 옆 audio_cache (forEngine이면 만든다).
        /// </summary>
        private string ResolveCacheDir(bool forEngine)
        {
            PluginSettings settings = Settings;
            string cacheDir = null;
            if (settings != null && !string.IsNullOrEmpty(settings.AudioCacheDir))
            {
                if (System.IO.Directory.Exists(settings.AudioCacheDir))
                    cacheDir = settings.AudioCacheDir;
                else if (forEngine)
                    FileLog.Warn("설정된 오디오 캐시 폴더가 없습니다: "
                                 + settings.AudioCacheDir + " — 자동 탐색으로 폴백");
            }
            if (cacheDir == null)
            {
                cacheDir = AudioSink.FindCacheDir(EngineExePath(),
                    settings != null ? settings.EngineArgs : null);
            }
            if (cacheDir == null)
            {
                // 동봉 캐시가 없어도 런타임 edge 합성 결과를 모아둘 곳은 필요하다
                try
                {
                    string dllDir = System.IO.Path.GetDirectoryName(
                        System.Reflection.Assembly.GetExecutingAssembly().Location);
                    if (dllDir != null)
                    {
                        cacheDir = System.IO.Path.Combine(dllDir, "audio_cache");
                        if (forEngine)
                            System.IO.Directory.CreateDirectory(cacheDir);
                    }
                }
                catch (Exception) { cacheDir = null; }
            }
            return cacheDir;
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

        /// <summary>내장(C#) 엔진 초기화 — 분석기 9종 + 보이스 워커.</summary>
        private void InitBuiltin()
        {
            // 이전 세션(SimHub 크래시 등)이 남긴 파이썬 엔진이 있으면 함께
            // 말하게 된다 — 이 플러그인이 띄운 엔진만 골라 정리
            _engine.KillOrphans(null, EngineStatus.DefaultPath());

            // 멘트 언어 — 분석기/멘트 풀 생성 전에 정해야 한다
            TeamRadio56.Core.Logic.Messages.SetLanguage(Settings.VoiceLanguage);

            var cfg = new EngineSettings
            {
                CooldownScale = Settings.CooldownScale,
                RequireRealtime = Settings.RequireRealtime,
            };
            cfg.Traffic.AlongsideM = Settings.AlongsideMeters;
            cfg.Traffic.StartSpotterSec = Settings.StartSpotterSeconds;
            cfg.Traffic.SideInvert = Settings.SideInvert;
            cfg.Traffic.RaceOnly = Settings.TrafficRaceOnly;

            _chief = new CrewChiefEngine(cfg,
                Settings.LapTimeEveryLap, Settings.StatusEveryLaps);

            VoiceKind kind;
            string note;
            AudioSink player;
            _radio = CreateRadio(null, out player, out kind, out note);
            _player = player;
            _voiceNote = note;
            _voiceMissing = -1;
            _voice = new VoiceWorker(_chief.Bus, _radio);
            _voice.Enabled = Settings.VoiceEnabled;
            _voice.Start();
            ClaimVoice(true);

            VoiceCache cache = _radio.Route.Cache;
            string cacheDir = cache.CacheDir;
            int cacheFiles = 0, segFiles = 0;
            if (cacheDir != null)
            {
                try
                {
                    cacheFiles = System.IO.Directory
                        .GetFiles(cacheDir, "*_rfx*.wav").Length;
                    segFiles = System.IO.Directory
                        .GetFiles(cacheDir, "*" + VoiceCache.SegSuffix).Length;
                }
                catch (Exception) { }
            }
            BuiltinCacheDir = cacheDir;
            BuiltinCacheCount = cacheFiles;
            FileLog.Info("내장(C#) 엔진 모드 — 분석기 9종 활성, 멘트 언어 {0}. "
                + "오디오 캐시: {1}",
                TeamRadio56.Core.Logic.Messages.Lang,
                cacheDir == null ? "(없음 — edge 목소리)"
                                 : cacheDir + " (무전 효과본 " + cacheFiles + "개, 문장 세그먼트 "
                                   + segFiles + "개, 조립 자산 "
                                   + (cache.SegmentAssetsPresent ? "있음" : "없음") + ")");
            if (kind == VoiceKind.Kokoro)
                CountMissingLater(_radio);
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
                        PhraseLines.TrackNamesEn, out whole, out seg);
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
        /// 세션 목소리 하나로 말하는 발화기를 만든다 — 엔진 시작 때 한 번 정하고
        /// 콜마다 바꾸지 않는다. forced가 있으면 그 목소리로 (파이썬 모드 [테스트 발화]).
        /// </summary>
        private RadioVoice CreateRadio(VoiceKind? forced, out AudioSink player,
                                       out VoiceKind kind, out string note)
        {
            string cacheDir = ResolveCacheDir(true);
            // 파이썬 build_engine과 같은 자동 보정 — 멘트 언어와 보이스 불일치,
            // kokoro는 영어 전용이라 ko 멘트는 edge로
            bool ko = string.Equals(Settings.VoiceLanguage, "ko",
                                    StringComparison.OrdinalIgnoreCase);
            string edgeVoice = Settings.EdgeVoice ?? "en-GB-RyanNeural";
            if (ko && !edgeVoice.StartsWith("ko"))
                edgeVoice = "ko-KR-InJoonNeural";
            else if (!ko && edgeVoice.StartsWith("ko"))
                edgeVoice = "en-GB-RyanNeural";
            note = null;
            if (forced.HasValue)
            {
                kind = forced.Value;
            }
            else
            {
                var probe = new VoiceCache(cacheDir, edgeVoice, Settings.SpeechRatePercent,
                                           "bm_george", true, "kokoro");
                string packState = ko ? VoicePolicy.PackNone
                                      : VoicePolicy.KokoroPackState(cacheDir, probe);
                kind = VoicePolicy.ResolveKind(Settings.VoiceEngine, ko, packState, false, out note);
                FileLog.Info("목소리: " + VoicePolicy.KindName(kind)
                             + (note != null ? " — " + note : "")
                             + (kind == VoiceKind.Edge ? " (" + edgeVoice + ")" : ""));
            }
            bool allow = Settings.AllowVoiceFallback;
            VoiceRoute route = VoicePolicy.CreateRoute(cacheDir, edgeVoice, Settings.SpeechRatePercent,
                                                       kind, Settings.RadioFx, allow);
            player = new AudioSink(Settings.Volume);
            return new RadioVoice(route, player,
                                  VoicePolicy.FallbackAllowed(allow, ko) ? Speech() : null);
        }

        /// <summary>Windows 음성 — 목소리 섞임 허용(en)일 때만 처음 쓸 때 만든다.</summary>
        private SpeechOutput Speech()
        {
            lock (_speechGate)
            {
                if (_speech == null)
                    _speech = new SpeechOutput();
                return _speech;
            }
        }

        public void End(PluginManager pluginManager)
        {
            FileLog.Info("종료 — 정리 중");
            // 진행 중인 다운로드는 취소 — DLL 교체 단계는 짧아 끝날 때까지 잠깐 기다린다
            UpdateController updater = _updater;
            if (updater != null)
                updater.Shutdown(TimeSpan.FromSeconds(3));
            // 백그라운드 모드 전환과 겹치면 End 뒤에 엔진이 새로 떠 고아가 된다
            lock (_restartGate)
            {
                _ending = true;
                SaveSettings();
                _engine.Dispose();       // 엔진 트리 종료 + 작업 개체 해제
                ShutdownBuiltin();
                ClaimVoice(false);
            }
            SpeechOutput speech;
            lock (_speechGate)
            {
                speech = _speech;
                _speech = null;
            }
            if (speech != null)
                speech.Dispose();
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
            this.AttachDelegate("EngineMode", () => UsingPythonEngine ? "python" : "builtin");
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

        /// <summary>최근 무전을 목소리 없이 자막으로만 냈는가 (목소리를 섞지 않으려고).</summary>
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
            if (UsingPythonEngine)
            {
                string issues = _engineStatus.Get("issues") ?? "";
                return ("," + issues + ",").Contains("," + key + ",");
            }
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
            bool python = UsingPythonEngine;
            RadioVoice radio = python ? null : _radio;
            AudioSink transient = null;
            VoiceKind kind;
            if (radio == null)
            {
                // 파이썬 모드(또는 엔진이 아직 없음) — 엔진과 같은 목소리로 한 번만 만든다
                if (python)
                    TeamRadio56.Core.Logic.Messages.SetLanguage(Settings.VoiceLanguage);
                VoiceKind? forced = null;
                if (python && _engineStatus.IsFresh)
                {
                    string v = _engineStatus.Voice;
                    if (string.Equals(v, "kokoro", StringComparison.OrdinalIgnoreCase))
                        forced = VoiceKind.Kokoro;
                    else if (string.Equals(v, "edge", StringComparison.OrdinalIgnoreCase))
                        forced = VoiceKind.Edge;
                }
                string note;
                try
                {
                    radio = CreateRadio(forced, out transient, out kind, out note);
                }
                catch (Exception ex)
                {
                    FileLog.Error("[테스트 발화] 준비 실패", ex);
                    return;
                }
            }
            // 모든 팩(v015 포함)에 통문장으로 들어 있는 멘트 풀 문구 — 멘트 언어로
            string text = "Box box, box box.";
            try
            {
                string[] lines = new TeamRadio56.Core.Logic.PhrasePool()
                    .Lines("pit_call", "casual");
                if (lines.Length > 0)
                    text = lines[0];
            }
            catch (Exception) { }
            string chosen = text;
            RadioVoice r = radio;
            AudioSink owned = transient;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // 명시적 사용자 동작이라 음성 출력 끔과 무관하게 말한다
                    SpeakResult result = r.Speak(chosen, "casual", false);
                    string kindName = VoicePolicy.KindName(r.Kind);
                    string detail = result == SpeakResult.Played ? r.LastSource : r.LastReason;
                    _testResult = result == SpeakResult.Dropped
                        ? string.Format(Loc.L("test_speak_dropped"), kindName, detail ?? "?")
                        : result == SpeakResult.Played
                            ? string.Format(Loc.L("test_speak_played"), kindName)
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

        public string[] RecentCalls()
        {
            RecentCall[] items = RecentCallItems();
            var lines = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
                lines[i] = items[i].Line;
            return lines;
        }

        /// <summary>최근 무전 (말하지 않은 콜 표시 포함) — 두 엔진 모드 공통.</summary>
        public RecentCall[] RecentCallItems()
        {
            if (UsingPythonEngine)
                return _engineStatus.RecentCallItems();
            VoiceWorker voice = _voice;
            return voice != null ? voice.RecentCallItems() : new RecentCall[0];
        }

        /// <summary>설정 화면 상태 블록용 — 세션 목소리와 무음 건수.</summary>
        public sealed class VoiceInfo
        {
            public string Kind;
            public string Note;
            public int Dropped;
            public int Missing = -1;
            public bool? LlmOn;
            public bool? LlmVoice;
        }

        /// <summary>지금 엔진의 목소리 정보. 엔진이 없거나 아직 모르면 null.</summary>
        public VoiceInfo CurrentVoiceInfo()
        {
            if (UsingPythonEngine)
            {
                if (!_engineStatus.IsFresh || string.IsNullOrEmpty(_engineStatus.Voice))
                    return null;
                return new VoiceInfo
                {
                    Kind = _engineStatus.Voice,
                    Note = _engineStatus.VoiceNote,
                    Dropped = _engineStatus.VoiceDropped,
                    Missing = _engineStatus.VoiceMissing,
                    LlmOn = _engineStatus.LlmOn,
                    LlmVoice = _engineStatus.LlmVoice,
                };
            }
            RadioVoice radio = _radio;
            if (radio == null)
                return null;
            return new VoiceInfo
            {
                Kind = VoicePolicy.KindName(radio.Kind),
                Note = _voiceNote,
                Dropped = radio.Drops.Count,
                Missing = _voiceMissing,
            };
        }

        // -- 내부 ------------------------------------------------------------

        /// <summary>
        /// 엔진 모드: 텔레메트리는 파이썬이 읽는다. 플러그인은 상태 파일만
        /// 확인해 UI에 보여주고, 엔진이 죽었으면 알린다.
        /// </summary>
        private void TickEngineMode()
        {
            DateTime now = DateTime.UtcNow;
            if (now < _nextStatusRead)
                return;
            _nextStatusRead = now.AddSeconds(1.0);

            _engineStatus.Refresh(EngineStatus.DefaultPath());

            if (!_engine.IsRunning)
            {
                StatusText = _engine.LastError ?? "엔진이 실행 중이 아닙니다 — [엔진 시작]을 눌러주세요";
                return;
            }
            if (!_engineStatus.IsFresh)
            {
                StatusText = "엔진 시작 중...";
                return;
            }
            StatusText = _engineStatus.Summary() ?? "엔진 동작 중";
        }

        private void Tick()
        {
            if (UsingPythonEngine)
            {
                TickEngineMode();
                // 오버레이 프로퍼티는 엔진 모드에서도 공유 메모리에서 직접
                // (스포터 표시는 1초 주기 상태 파일로는 늦다)
                UpdateOverlay(_reader.Poll());
                _wasConnected = _reader.Connected;
                return;
            }

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

            // RestartEngine(UI 스레드)이 도중에 갈아끼울 수 있어 로컬로 잡는다
            CrewChiefEngine chief = _chief;
            VoiceWorker voice = _voice;
            if (chief == null)
                return;
            if (voice != null && Settings != null)
                voice.Enabled = Settings.VoiceEnabled;
            chief.OnPoll(snap, _reader.Connected);
            StatusText = chief.StatusText;
        }
    }
}
