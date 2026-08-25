using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/racecontrol.py 포팅 — 코스 상태/레이스 진행.
    ///
    /// FCY 진입·해제와 FCY 중 피트 오픈, 섹터 로컬 옐로(엣지 트리거),
    /// 레이스 스타트/체커, 남은 시간 마일스톤/마지막 랩, 피트 리미터 경고,
    /// 블루 플래그, 페널티(게임 메시지에서 종류/사유 파싱), 클래스 순위 변동.
    /// </summary>
    public sealed class RaceControlAnalyzer
    {
        private const int PhaseGreen = 5;
        private const int PhaseFcy = 6;
        private const int PhaseOver = 8;
        private const int YPitClosed = 2;
        private const int YPitLeadLap = 3;
        private const int YPitOpen = 4;

        private static readonly int[] MilestonesMin = { 60, 30, 10 };
        private const double PitLimitMarginKmh = 5.0;
        private const double DefaultPitLimitKmh = 80.0;
        private const double PenaltyWaitSec = 1.5;   // 종류 메시지 대기

        private readonly bool _sectorCalls;

        private int? _phase;
        private int? _yellow;
        private byte[] _prevSectorFlags;
        private readonly Dictionary<int, double> _sectorYellowAnnounced =
            new Dictionary<int, double>();
        private readonly HashSet<int> _milestonesDone = new HashSet<int>();
        private double? _initialRemaining;
        private bool _finalLapDone;
        private bool _raceStarted;
        private int? _classPlace;
        private double _limiterWarnedT;
        private bool _blueFlag;
        private int? _penalties;               // 미소화 페널티 수 (null=기준 미확보)
        private readonly List<KeyValuePair<double, string>> _recentMsgs =
            new List<KeyValuePair<double, string>>();
        private string _lastStatusMsg = "";
        private string _lastHistoryMsg = "";
        private double? _penDue;               // 페널티 콜 예정 시각
        private int _penCount;

        public RaceControlAnalyzer(bool sectorYellowCalls = true)
        {
            _sectorCalls = sectorYellowCalls;
        }

        public void Reset()
        {
            _phase = null;
            _yellow = null;
            _prevSectorFlags = null;
            _sectorYellowAnnounced.Clear();
            _milestonesDone.Clear();
            _initialRemaining = null;
            _finalLapDone = false;
            _raceStarted = false;
            _classPlace = null;
            _limiterWarnedT = 0.0;
            _blueFlag = false;
            _penalties = null;
            _recentMsgs.Clear();
            _lastStatusMsg = "";
            _lastHistoryMsg = "";
            _penDue = null;
            _penCount = 0;
        }

        /// <summary>게임 메시지에서 페널티 종류/사유 추출. 아니면 null.</summary>
        public static string[] ParsePenalty(string text)
        {
            string tl = (text ?? "").ToLowerInvariant();
            bool hit = tl.Contains("penalty") || tl.Contains("drive thru")
                || tl.Contains("drive-thru") || tl.Contains("drive through")
                || tl.Contains("stop/go") || tl.Contains("stop go");
            if (!hit)
                return null;
            string kind;
            if (tl.Contains("drive"))
                kind = "drive-through";
            else if (tl.Contains("stop"))
                kind = "stop-and-go";
            else if (tl.Contains("second") || tl.Contains("sec ") || tl.Contains("time"))
                kind = "time penalty";
            else
                kind = "penalty";
            string reason = "";
            if (tl.Contains("pit") && (tl.Contains("speed") || tl.Contains("spd")))
                reason = "pit lane speeding";
            else if (tl.Contains("cut") || tl.Contains("track limit") || tl.Contains("boundar"))
                reason = "track limits";
            else if (tl.Contains("yellow") || tl.Contains("full course") || tl.Contains("caution"))
                reason = "yellow flag infringement";
            else if (tl.Contains("false start") || tl.Contains("jump"))
                reason = "start infringement";
            else if (tl.Contains("contact") || tl.Contains("avoidable"))
                reason = "contact";
            else if (tl.Contains("blocking"))
                reason = "blocking";
            else if (tl.Contains("rejoin"))
                reason = "unsafe rejoin";
            return new[] { kind, reason };
        }

        // -- 5Hz 틱 -----------------------------------------------------------

        public void OnTick(RaceState state, Snapshot snap, EventBus bus)
        {
            SessionInfo ses = snap.Session;
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return;
            int phase = ses.GamePhase;
            int yellow = ses.YellowState;

            if (_phase.HasValue && phase != _phase.Value)
                OnPhaseChange(_phase.Value, phase, state, bus);
            if (state.IsRace && phase == PhaseFcy
                && _yellow.HasValue && yellow != _yellow.Value)
            {
                OnYellowChange(_yellow.Value, yellow, state, bus);
            }
            _phase = phase;
            _yellow = yellow;

            CheckSectorYellow(ses, snap.T, bus);
            CheckPitLimiter(ses, snap, bus);
            CheckBlueFlag(me, state, bus);
            CollectMessages(ses, snap.T);
            CheckPenalties(me, snap.T, state, bus);
            if (state.IsRace && phase == PhaseGreen)
                CheckTimeMilestones(state, ses, bus);
        }

        private void OnPhaseChange(int old, int now, RaceState state, EventBus bus)
        {
            // 레이스 스타트: 포메이션/카운트다운 → 그린
            if (now == PhaseGreen && (old == 3 || old == 4) && state.IsRace)
            {
                _raceStarted = true;
                var ev = new RadioEvent
                {
                    Type = EventTypes.RaceStart,
                    Priority = Priority.Critical,
                    Tone = "urgent",
                    Ttl = 6.0,
                };
                ev.Data["pool"] = "race_start";
                bus.Push(ev);
                state.AddNarrative("(이벤트) 레이스 스타트");
            }
            // FCY/세이프티카 발동
            else if (now == PhaseFcy)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.Fcy,
                    Priority = Priority.Critical,
                    Tone = "urgent",
                    Ttl = 8.0,
                    BridgeTopic = "풀코스옐로(세이프티카) 발동. 피트가 열리면 "
                                  + "시간 손실 없이 피트할 기회. 연료/타이어 상태와 엮어 "
                                  + "전략 조언을 해라.",
                };
                ev.Data["pool"] = "fcy_start";
                bus.Push(ev);
                state.SetIssue("fcy", "풀코스옐로 진행 중 (피트 전략 기회)");
                state.AddNarrative("(이벤트) FCY 발동");
            }
            // FCY 해제 → 그린 (리스타트)
            else if (now == PhaseGreen && old == PhaseFcy)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.GreenFlag,
                    Priority = Priority.Critical,
                    Tone = "urgent",
                    Ttl = 6.0,
                };
                ev.Data["pool"] = "green_flag";
                bus.Push(ev);
                state.ClearIssue("fcy");
                state.AddNarrative("(이벤트) 리스타트 그린");
            }
            // 체커
            else if (now == PhaseOver && state.IsRace && _raceStarted)
            {
                int place = _classPlace
                    ?? (state.Laps.Count > 0 ? state.Laps[state.Laps.Count - 1].ClassPlace : 0);
                var ev = new RadioEvent
                {
                    Type = EventTypes.RaceEnd,
                    Priority = Priority.High,
                    Ttl = 30.0,
                };
                ev.Data["pool"] = "race_end";
                ev.Data["class_place"] = place;
                bus.Push(ev);
                state.AddNarrative("(이벤트) 체커, 클래스 P" + place);
            }
        }

        private void OnYellowChange(int old, int now, RaceState state, EventBus bus)
        {
            // FCY 중 피트 개방 전이 — 내구레이스에서 가장 돈이 되는 콜
            if ((old == YPitClosed || old == YPitLeadLap) && now == YPitOpen)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.FcyPitOpen,
                    Priority = Priority.Critical,
                    Tone = "urgent",
                    Ttl = 10.0,
                    BridgeTopic = "FCY 중 피트가 방금 열렸다. 지금 들어오면 "
                                  + "시간 손실이 최소다. 연료/타이어 상황 기준으로 "
                                  + "들어올지 말지 판단을 말해라.",
                };
                ev.Data["pool"] = "fcy_pit_open";
                bus.Push(ev);
                state.AddNarrative("(이벤트) FCY 피트 오픈");
            }
        }

        private void CheckSectorYellow(SessionInfo ses, double now, EventBus bus)
        {
            // 엣지 트리거: 0 → (1|2) 전이만 콜. 첫 샘플/쓰레기 값(>2)은 무시.
            if (!_sectorCalls)
                return;
            byte[] flags = ses.SectorFlags ?? new byte[0];
            byte[] prev = _prevSectorFlags;
            int n = Math.Min(flags.Length, 3);
            var cur = new byte[n];
            Array.Copy(flags, cur, n);
            _prevSectorFlags = cur;
            if (prev == null)
                return;
            for (int i = 0; i < n; i++)
            {
                int was = i < prev.Length ? prev[i] : 0;
                int flag = cur[i];
                if (!(was <= 0 && flag > 0 && flag <= 2))
                    continue;
                double last;
                if (_sectorYellowAnnounced.TryGetValue(i, out last) && now - last < 120)
                    continue;
                _sectorYellowAnnounced[i] = now;
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.SectorYellow,
                    Priority = Priority.High,
                    Message = "Yellow in sector " + (i + 1)
                              + ". No overtaking, be ready to lift.",
                    DedupKey = "syellow_" + i,
                    Ttl = 15.0,
                    Tone = "urgent",
                });
            }
        }

        private void CheckBlueFlag(VehicleInfo me, RaceState state, EventBus bus)
        {
            // 내게 블루 플래그가 게시된 순간(mFlag=6 엣지) 양보 안내
            bool blue = me.FlagBlue;
            if (blue && !_blueFlag)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.BlueFlag,
                    Priority = Priority.High,
                    DedupKey = "blue_flag",
                    Tone = "urgent",
                    Ttl = 6.0,
                };
                ev.Data["pool"] = "blue_flag";
                bus.Push(ev);
                state.AddNarrative("(이벤트) 블루 플래그 — 랩 앞선 차에 양보");
            }
            _blueFlag = blue;
        }

        private void CollectMessages(SessionInfo ses, double now)
        {
            string status = (ses.StatusMessage ?? "").Trim();
            if (status.Length > 0 && status != _lastStatusMsg)
            {
                _lastStatusMsg = status;
                _recentMsgs.Add(new KeyValuePair<double, string>(now, status));
            }
            string history = (ses.HistoryMessage ?? "").Trim();
            if (history.Length > 0 && history != _lastHistoryMsg)
            {
                _lastHistoryMsg = history;
                _recentMsgs.Add(new KeyValuePair<double, string>(now, history));
            }
            // 10초 지난 메시지는 버린다
            _recentMsgs.RemoveAll(kv => now - kv.Key > 10.0);
        }

        private void CheckPenalties(VehicleInfo me, double now, RaceState state,
                                    EventBus bus)
        {
            int n = me.NumPenalties;
            if (!_penalties.HasValue)
            {
                _penalties = n;
                if (n > 0)
                    state.SetIssue("penalty", "미소화 페널티 " + n + "건");
                return;
            }
            if (n > _penalties.Value)
            {
                // 종류 메시지가 카운트보다 늦게 뜰 수 있어 잠깐 기다렸다 콜
                _penDue = now + PenaltyWaitSec;
                _penCount = n;
                state.SetIssue("penalty", "미소화 페널티 " + n + "건");
            }
            else if (n < _penalties.Value && n == 0)
            {
                _penDue = null;
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.Penalty,
                    Priority = Priority.Normal,
                    Message = "Penalty served. Back to your race.",
                    DedupKey = "pen_clear",
                    Ttl = 20.0,
                    Tone = "casual",
                });
                state.ClearIssue("penalty");
                state.AddNarrative("(이벤트) 페널티 소화 완료");
            }
            _penalties = n;

            if (_penDue.HasValue && now >= _penDue.Value)
            {
                _penDue = null;
                EmitPenalty(_penCount, state, bus);
            }
        }

        private void EmitPenalty(int n, RaceState state, EventBus bus)
        {
            string[] detail = null;
            for (int i = _recentMsgs.Count - 1; i >= 0; i--)
            {
                detail = ParsePenalty(_recentMsgs[i].Value);
                if (detail != null)
                    break;
            }
            string message;
            string issue;
            string topic;
            if (detail != null)
            {
                string kind = detail[0];
                string reason = detail[1];
                string head = "Penalty — " + kind
                    + (reason.Length > 0 ? ", " + reason : "");
                string advice;
                switch (kind)
                {
                    case "drive-through":
                        advice = "Serve it next lap. Mind the limiter.";
                        break;
                    case "stop-and-go":
                        advice = "Hold the stop time in the box. Stay calm.";
                        break;
                    case "time penalty":
                        advice = "Added to the result. We claw it back on pace.";
                        break;
                    default:
                        advice = "I'll call the timing.";
                        break;
                }
                message = head + ". " + advice;
                issue = "미소화 페널티 " + n + "건 (" + kind
                        + (reason.Length > 0 ? ", " + reason : "") + ")";
                topic = "방금 페널티가 부여됐다: " + kind + ", 사유 "
                        + (reason.Length > 0 ? reason : "불명")
                        + ". 다음 피트와 엮어 언제 소화할지 판단을 짧게.";
            }
            else
            {
                message = null;                  // 일반 풀로 폴백
                issue = "미소화 페널티 " + n + "건";
                topic = "방금 페널티가 부여됐다 (미소화 " + n + "건). "
                        + "다음 피트와 엮어 언제 소화할지 판단을 짧게.";
            }
            var ev = new RadioEvent
            {
                Type = EventTypes.Penalty,
                Priority = Priority.Critical,
                Message = message,
                DedupKey = "pen_" + n,
                Tone = "urgent",
                Ttl = 10.0,
                BridgeTopic = topic,
            };
            ev.Data["pool"] = "penalty";
            bus.Push(ev);
            state.SetIssue("penalty", issue);
            state.AddNarrative("(이벤트) 페널티 부여 — " + issue);
        }

        private void CheckPitLimiter(SessionInfo ses, Snapshot snap, EventBus bus)
        {
            PlayerInfo p = snap.Player;
            if (p == null || !p.InPitLane || p.SpeedLimiter)
                return;
            double limit = ses.PitSpeedLimitKmh;
            if (limit <= 1.0)
                limit = DefaultPitLimitKmh;
            if (p.SpeedKmh > limit + PitLimitMarginKmh)
            {
                if (snap.T - _limiterWarnedT < 10.0)
                    return;
                _limiterWarnedT = snap.T;
                var ev = new RadioEvent
                {
                    Type = EventTypes.PitLimiter,
                    Priority = Priority.Critical,
                    Tone = "urgent",
                    Ttl = 3.0,
                };
                ev.Data["pool"] = "pit_limiter";
                bus.Push(ev);
            }
        }

        private void CheckTimeMilestones(RaceState state, SessionInfo ses, EventBus bus)
        {
            if (ses.EndEt <= 0)
                return;
            double remaining = ses.EndEt - ses.CurrentEt;
            if (remaining <= 0)
                return;
            if (!_initialRemaining.HasValue)
                _initialRemaining = remaining;
            foreach (int minutes in MilestonesMin)
            {
                if (_milestonesDone.Contains(minutes))
                    continue;
                // 레이스 총 길이보다 크거나 비슷한 마일스톤은 무의미
                if (minutes * 60 > _initialRemaining.Value - 120)
                {
                    _milestonesDone.Add(minutes);
                    continue;
                }
                if (remaining <= minutes * 60)
                {
                    _milestonesDone.Add(minutes);
                    // 너무 늦게 붙은 경우(세션 중간 시작 등) 근접 마일스톤만 발화
                    if (remaining > minutes * 60 - 90)
                    {
                        var ev = new RadioEvent
                        {
                            Type = EventTypes.RaceMilestone,
                            Priority = Priority.Normal,
                            DedupKey = "ms_" + minutes,
                            Ttl = 45.0,
                        };
                        ev.Data["remaining_min"] = minutes;
                        bus.Push(ev);
                    }
                }
            }
            // 마지막 랩: 남은 시간이 평소 랩타임보다 짧아진 순간
            double? baseLap = state.BaselineLapTime();
            if (!_finalLapDone && baseLap.HasValue && baseLap.Value > 0
                && remaining < baseLap.Value)
            {
                _finalLapDone = true;
                var ev = new RadioEvent
                {
                    Type = EventTypes.RaceMilestone,
                    Priority = Priority.High,
                    Message = "Last lap. Bring it home.",
                    DedupKey = "final_lap",
                    Ttl = 30.0,
                };
                ev.Data["final_lap"] = true;
                bus.Push(ev);
                state.AddNarrative("(이벤트) 마지막 랩");
            }
        }

        // -- 랩 완료: 클래스 순위 변동 -----------------------------------------

        public void OnLap(RaceState state, Snapshot snap, EventBus bus)
        {
            VehicleInfo me = snap.PlayerScoring();
            if (me == null || !state.IsRace)
                return;
            int cp = RaceState.ClassPlaceOf(snap, me);
            int? prev = _classPlace;
            _classPlace = cp;
            if (!prev.HasValue || cp == prev.Value)
                return;
            // 피트 사이클 중 순위 출렁임은 무시 (내가 피트 랩이면 침묵)
            if (state.Laps.Count > 0 && state.Laps[state.Laps.Count - 1].InPits)
                return;
            string pool = cp < prev.Value ? "position_up" : "position_down";
            var ev = new RadioEvent
            {
                Type = EventTypes.PositionChange,
                Priority = Priority.Normal,
                DedupKey = "pos_" + cp,
                Ttl = 30.0,
                Tone = "casual",
            };
            ev.Data["pool"] = pool;
            ev.Data["class_place"] = cp;
            bus.Push(ev);
            state.AddNarrative("(이벤트) 클래스 P" + prev.Value + "→P" + cp);
            state.SetIssue("position", "현재 클래스 P" + cp);
        }
    }
}
