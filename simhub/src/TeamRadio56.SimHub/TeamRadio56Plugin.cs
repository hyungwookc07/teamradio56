using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using TeamRadio56.Core.Config;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Telemetry;

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
        public const string Version = "0.8.1-simhub-ui";

        private const double PollHz = 5.0;
        private const int RecentCallsKept = 5;

        private readonly SharedMemoryReader _reader = new SharedMemoryReader();
        private readonly Queue<string> _recent = new Queue<string>();
        private readonly object _recentGate = new object();

        private SpeechOutput _speech;
        private DateTime _nextPoll = DateTime.MinValue;
        private bool _wasConnected;
        private bool _layoutOk;
        private string _loggedGameName;

        public PluginManager PluginManager { get; set; }

        /// <summary>설정 — SimHub 좌측 메뉴에서 편집, DLL 옆 파일에 저장.</summary>
        public PluginSettings Settings { get; private set; }

        public bool IsConnected { get { return _reader.Connected; } }

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

            string layoutError = SharedMemoryReader.VerifyLayout();
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

        public void End(PluginManager pluginManager)
        {
            FileLog.Info("종료 — 정리 중");
            SaveSettings();
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
            Say("Radio check. Team radio online.");
        }

        public string[] RecentCalls()
        {
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

        private void Tick()
        {
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

            if (snap == null)
            {
                StatusText = "게임 대기 중 — LMU 실행 + 공유 메모리 플러그인 활성화 필요";
                return;
            }

            // 모니터/메뉴에선 침묵 (설정으로 끌 수 있음)
            if (Settings != null && Settings.RequireRealtime && !snap.Session.InRealtime)
            {
                StatusText = "모니터/메뉴 상태 — 주행 복귀 대기 중";
                return;
            }

            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
            {
                StatusText = string.Format("세션 대기 중 (차량 {0}대, 페이즈 {1})",
                    snap.Session.NumVehicles, snap.Session.GamePhase);
                return;
            }

            StatusText = string.Format(
                "{0} · 페이즈 {1} · P{2} {3} · 랩 {4} · 연료 {5:F1}L · {6:F0}km/h · 차량 {7}대",
                snap.Session.Track, snap.Session.GamePhase, me.Place, me.Class,
                me.TotalLaps,
                snap.Player != null ? snap.Player.Fuel : 0.0,
                snap.Player != null ? snap.Player.SpeedKmh : 0.0,
                snap.Session.NumVehicles);
        }
    }
}
