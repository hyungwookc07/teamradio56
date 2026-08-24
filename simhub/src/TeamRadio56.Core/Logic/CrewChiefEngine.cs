using System;
using System.Collections.Generic;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>내장 엔진 설정 — 플러그인 설정에서 필요한 값만 추린 것.</summary>
    public sealed class EngineSettings
    {
        public TrafficSettings Traffic = new TrafficSettings();
        public double CooldownScale = 1.0;
        public bool RequireRealtime = true;
    }

    /// <summary>
    /// main.py CrewChiefApp의 판단 루프 포팅 — 스냅샷 하나를 받아 세션
    /// 전이/브리핑/분석기 틱/랩 완료 체인을 돌리고 이벤트 버스를 채운다.
    /// 발화는 VoiceWorker가 버스에서 꺼내 처리한다 (LLM/브리지는 미이식 —
    /// 해당 이벤트는 렌더러가 침묵 처리).
    /// </summary>
    public sealed class CrewChiefEngine
    {
        public readonly RaceState State = new RaceState();
        public readonly EventBus Bus;

        private readonly EngineSettings _cfg;
        private readonly SessionBriefer _briefer = new SessionBriefer();
        private readonly TrafficAnalyzer _traffic;
        private readonly RaceControlAnalyzer _racecontrol = new RaceControlAnalyzer();
        private readonly RivalAnalyzer _rivals = new RivalAnalyzer();
        private readonly HealthAnalyzer _health = new HealthAnalyzer();
        private readonly FuelAnalyzer _fuel = new FuelAnalyzer();
        private readonly PaceAnalyzer _pace = new PaceAnalyzer();
        private readonly TyreAnalyzer _tyres = new TyreAnalyzer();
        private readonly StrategyEngine _strategy = new StrategyEngine();
        private readonly StatusReporter _reporter;

        private bool _wasInSession;

        /// <summary>설정 화면에 보여줄 한 줄 상태.</summary>
        public string StatusText { get; private set; } = "대기 중";

        public CrewChiefEngine(EngineSettings cfg,
                               bool laptimeEveryLap = false, int statusEveryLaps = 0)
        {
            _cfg = cfg ?? new EngineSettings();
            Bus = new EventBus(Cooldowns.Default(_cfg.CooldownScale));
            _traffic = new TrafficAnalyzer(_cfg.Traffic);
            _reporter = new StatusReporter(laptimeEveryLap, statusEveryLaps);
        }

        /// <summary>세션 종료/게임 연결 끊김 — 상태·버스·분석기 전부 리셋.</summary>
        public void ResetSession(string reason)
        {
            FileLog.Info("세션 마무리 ({0}) — 상태/버스/분석기 리셋", reason);
            State.Reset();
            Bus.Clear();
            _briefer.Reset();
            _traffic.Reset();
            _racecontrol.Reset();
            _rivals.Reset();
            _health.Reset();
            _pace.Reset();
            _strategy.Reset();
            _reporter.Reset();
        }

        /// <summary>
        /// 5Hz마다 호출. snap이 null이거나 연결이 끊겼으면 대기 처리만 한다.
        /// </summary>
        public void OnPoll(Snapshot snap, bool connected)
        {
            if (!connected || snap == null)
            {
                if (_wasInSession)
                {
                    ResetSession("게임 연결 끊김");
                    _wasInSession = false;
                }
                StatusText = "게임 대기 중 — LMU 실행 + 공유 메모리 플러그인 활성화 필요";
                return;
            }

            // 세션 시작/종료 전이
            if (_wasInSession && !snap.InSession)
            {
                ResetSession("세션 종료");
            }
            else if (!_wasInSession && snap.InSession)
            {
                FileLog.Info("세션 시작 감지 (트랙: {0})",
                    snap.Session != null ? snap.Session.Track : "?");
                _briefer.Reset();
            }
            _wasInSession = snap.InSession;

            if (!snap.InSession)
            {
                StatusText = "세션 대기 중 (게임 연결됨)";
                return;
            }
            if (_cfg.RequireRealtime && !snap.Session.InRealtime)
            {
                StatusText = "모니터/메뉴 — 주행 복귀 대기 중";
                return;
            }

            OnSnapshot(snap);

            VehicleInfo me = snap.PlayerScoring();
            if (me != null)
            {
                StatusText = string.Format(
                    "{0} · P{1} (클래스 P{2}) · 랩 {3} · 연료 {4:F1}L · {5:F0}km/h",
                    snap.Session.Track, me.Place,
                    RaceState.ClassPlaceOf(snap, me), me.TotalLaps,
                    snap.Player != null ? snap.Player.Fuel : 0.0,
                    snap.Player != null ? snap.Player.SpeedKmh : 0.0);
            }
        }

        /// <summary>main.py on_snapshot + on_lap_complete 포팅.</summary>
        private void OnSnapshot(Snapshot snap)
        {
            _briefer.MaybeBrief(State, snap, Bus);
            _traffic.OnTick(State, snap, Bus);
            _racecontrol.OnTick(State, snap, Bus);
            _rivals.OnTick(State, snap, Bus);
            _health.OnTick(State, snap, Bus);
            LapRecord lap = State.Update(snap);
            if (lap == null)
                return;

            Dictionary<string, object> fuelStatus = _fuel.OnLap(State, snap, Bus);
            _pace.OnLap(State, snap, Bus, lap);
            Dictionary<string, object> tyreStatus = _tyres.OnLap(State, snap, Bus);
            if (lap.InPits && State.IsRace)
            {
                Bus.Push(new RadioEvent
                {
                    Type = EventTypes.StintBriefing,
                    Priority = Priority.Normal,
                    DedupKey = "stint_" + lap.LapNumber,
                });
            }
            _strategy.OnLap(State, snap, Bus, fuelStatus, tyreStatus);
            _reporter.OnLap(State, snap, Bus, fuelStatus, tyreStatus);
            _racecontrol.OnLap(State, snap, Bus);
            _rivals.OnLap(State, snap, Bus);
            _health.OnLap(State, snap, Bus);
        }
    }
}
