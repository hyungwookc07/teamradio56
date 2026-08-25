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
    [PluginDescription("LMU AI 크루치프 — 상황을 판단해 영어 팀라디오로 불러준다")]
    [PluginAuthor("teamradio56")]
    [PluginName("teamradio56")]
    public class TeamRadio56Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        public const string Version = "0.11.0-i18n";

        private const double PollHz = 5.0;
        private const int RecentCallsKept = 5;

        private readonly Rf2SharedMemoryReader _reader = new Rf2SharedMemoryReader();
        private readonly Queue<string> _recent = new Queue<string>();
        private readonly object _recentGate = new object();

        private readonly EngineHost _engine = new EngineHost();
        private readonly EngineStatus _engineStatus = new EngineStatus();
        private SpeechOutput _speech;
        private CrewChiefEngine _chief;      // 내장(C#) 엔진 모드
        private VoiceWorker _voice;
        private AudioSink _sink;
        private DateTime _nextStatusRead = DateTime.MinValue;
        private DateTime _nextPoll = DateTime.MinValue;
        private bool _wasConnected;
        private bool _layoutOk;
        private string _loggedGameName;

        public PluginManager PluginManager { get; set; }

        /// <summary>설정 — SimHub 좌측 메뉴에서 편집, DLL 옆 파일에 저장.</summary>
        public PluginSettings Settings { get; private set; }

        public bool IsConnected
        {
            get { return UsingPythonEngine ? _engineStatus.GetBool("connected") : _reader.Connected; }
        }

        /// <summary>파이썬 엔진 모드로 동작 중인가 (C# 이식 완료 전 기본값).</summary>
        public bool UsingPythonEngine
        {
            get
            {
                return Settings != null
                    && !string.Equals(Settings.EngineMode, "builtin",
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
            SaveSettings();
            return _engine.Start(EngineExePath(),
                                 Settings != null ? Settings.EngineArgs : null,
                                 SettingsStore.Path, EngineStatus.DefaultPath());
        }

        public void StopEngine()
        {
            _engine.Stop();
        }

        /// <summary>
        /// 설정을 바꾼 뒤 엔진에 반영 (재시작). 모드 전환(python↔builtin)도
        /// 여기서 처리한다 — 이전 모드를 내리고 현재 모드로 다시 올린다.
        /// </summary>
        public void RestartEngine()
        {
            SaveSettings();
            _engine.Stop();          // 파이썬 엔진이 돌고 있으면 종료
            ShutdownBuiltin();       // 내장 엔진이 돌고 있으면 정리
            if (UsingPythonEngine)
                StartEngine();
            else
                InitBuiltin();
        }

        private void ShutdownBuiltin()
        {
            if (_voice != null)
            {
                _voice.Dispose();
                _voice = null;
            }
            _sink = null;
            _chief = null;
        }

        /// <summary>설정 화면에 보여줄 한 줄 상태.</summary>
        public string StatusText { get; private set; }

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

            _speech = new SpeechOutput();
            StatusText = "대기 중";

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

            string cacheDir = AudioSink.FindCacheDir(EngineExePath());
            var cache = new VoiceCache(cacheDir, Settings.EdgeVoice,
                Settings.SpeechRatePercent, "bm_george", Settings.RadioFx);
            _sink = new AudioSink(cache, _speech);
            _voice = new VoiceWorker(_chief.Bus, _sink);
            _voice.Enabled = Settings.VoiceEnabled;
            _voice.Start();

            FileLog.Info("내장(C#) 엔진 모드 — 분석기 9종 활성. 오디오 캐시: {0}",
                cacheDir ?? "(없음 — Windows TTS 폴백)");
            if (string.Equals(Settings.VoiceLanguage, "ko",
                              StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Warn("내장 엔진은 아직 영어 멘트만 지원합니다 — "
                             + "한국어 멘트는 python 엔진 모드를 사용하세요");
            }
        }

        public void End(PluginManager pluginManager)
        {
            FileLog.Info("종료 — 정리 중");
            SaveSettings();
            _engine.Stop();
            if (_voice != null)
                _voice.Dispose();
            if (_speech != null)
                _speech.Dispose();
            _reader.Dispose();
        }

        // -- 설정 화면에서 호출 ------------------------------------------------

        public void SaveSettings()
        {
            if (Settings != null)
                SettingsStore.Save(Settings);
        }

        public void TestSpeak()
        {
            // 이 줄이 로그에 없으면 버튼/UI 문제, 있는데 "발화 완료"가
            // 없으면 TTS 문제 — 무음 진단의 분기점
            FileLog.Info("[테스트 발화] 버튼 눌림");
            Say("Radio check. Team radio online.");
        }

        public string[] RecentCalls()
        {
            if (UsingPythonEngine)
                return _engineStatus.RecentCalls();
            if (_voice != null)
                return _voice.RecentCalls();
            lock (_recentGate)
            {
                return _recent.ToArray();
            }
        }

        // -- 내부 ------------------------------------------------------------

        private void Say(string text)
        {
            if (Settings != null && !Settings.VoiceEnabled)
            {
                FileLog.Info("(음성 꺼짐) " + text);
                return;
            }
            _speech.Say(text);
            lock (_recentGate)
            {
                _recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
                while (_recent.Count > RecentCallsKept)
                    _recent.Dequeue();
            }
        }

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
                return;
            }

            Snapshot snap = _reader.Poll();

            if (_reader.Connected != _wasConnected)
            {
                _wasConnected = _reader.Connected;
                FileLog.Info(_reader.Connected
                    ? "LMU 공유 메모리 연결됨"
                    : "LMU 공유 메모리 끊김 (게임 종료/일시정지)");
                if (_reader.Connected)
                    Say("Radio check. Team radio online.");
            }

            if (_chief == null)
                return;
            if (_voice != null && Settings != null)
                _voice.Enabled = Settings.VoiceEnabled;
            _chief.OnPoll(snap, _reader.Connected);
            StatusText = _chief.StatusText;
        }
    }
}
