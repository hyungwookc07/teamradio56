using System;
using GameReaderCommon;
using SimHub.Plugins;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// teamradio56 SimHub 플러그인 (1단계 골격).
    ///
    /// 이 단계의 목표는 기능이 아니라 검증이다:
    ///   1) SimHub에 로드되는가
    ///   2) 생성된 rF2 구조체 레이아웃이 맞는가 (크기 대조)
    ///   3) LMU 공유 메모리를 직접 읽는가
    ///   4) 소리가 나가는가 (Windows 내장 TTS로 확인)
    /// 분석기/멘트 풀/무전 효과는 다음 단계에서 Core에 이식한다.
    ///
    /// 텔레메트리는 SimHub 정규화 데이터가 아니라 공유 메모리에서 직접 읽는다.
    /// LMU 전용 필드(덴트 존/충격 크기/pathLateral/횡속도/블루플래그)가
    /// 정규화 계층에는 없기 때문. SimHub은 호스팅(자동 실행/설정 UI/배포) 담당.
    /// </summary>
    [PluginDescription("LMU AI 크루치프 — 상황을 판단해 영어 팀라디오로 불러준다")]
    [PluginAuthor("teamradio56")]
    [PluginName("teamradio56")]
    public class TeamRadio56Plugin : IPlugin, IDataPlugin
    {
        public const string Version = "0.8.0-simhub-stage1";

        private const double PollHz = 5.0;

        private readonly SharedMemoryReader _reader = new SharedMemoryReader();
        private SpeechOutput _speech;
        private DateTime _nextPoll = DateTime.MinValue;
        private DateTime _lastStatusLog = DateTime.MinValue;
        private bool _wasConnected;
        private bool _layoutOk;
        private string _loggedGameName;

        public PluginManager PluginManager { get; set; }

        public void Init(PluginManager pluginManager)
        {
            FileLog.Banner(Version);

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
            FileLog.Info("초기화 완료. 로그 파일: " + FileLog.Path);
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
                Tick(now);
            }
            catch (Exception ex)
            {
                FileLog.Error("틱 처리 실패", ex);
            }
        }

        private void Tick(DateTime now)
        {
            Snapshot snap = _reader.Poll();

            if (_reader.Connected != _wasConnected)
            {
                _wasConnected = _reader.Connected;
                FileLog.Info(_reader.Connected
                    ? "LMU 공유 메모리 연결됨"
                    : "LMU 공유 메모리 끊김 (게임 종료/일시정지)");
                if (_reader.Connected)
                    _speech.Say("Radio check. Team radio online.");
            }

            if (snap == null)
                return;

            // 1단계에서는 읽은 값이 말이 되는지만 확인 (5초 간격)
            if ((now - _lastStatusLog).TotalSeconds < 5.0)
                return;
            _lastStatusLog = now;

            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
            {
                FileLog.Info("세션 대기 중 (차량 {0}대, 페이즈 {1})",
                    snap.Session.NumVehicles, snap.Session.GamePhase);
                return;
            }

            FileLog.Info(
                "[{0}] 페이즈 {1} | P{2} {3} | 랩 {4} | {5:F0}m | 연료 {6:F1}L | {7:F0}km/h | 차량 {8}대",
                snap.Session.Track, snap.Session.GamePhase, me.Place, me.Class,
                me.TotalLaps, me.LapDist,
                snap.Player != null ? snap.Player.Fuel : 0.0,
                snap.Player != null ? snap.Player.SpeedKmh : 0.0,
                snap.Session.NumVehicles);
        }

        public void End(PluginManager pluginManager)
        {
            FileLog.Info("종료 — 정리 중");
            if (_speech != null)
                _speech.Dispose();
            _reader.Dispose();
        }
    }
}
