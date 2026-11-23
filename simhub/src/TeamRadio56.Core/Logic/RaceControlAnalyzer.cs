using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/racecontrol.py 포팅 — 코스 상태/레이스 진행.
    ///
    /// FCY 진입·해제와 FCY 중 피트 오픈, 섹터 로컬 옐로(엣지 트리거),
    /// 레이스 스타트, 남은 시간 마일스톤, 마지막 랩/내 피니시 체커(판단은 RaceEndTracker),
    /// 체커 뒤 무전(쿨다운 랩 → 요약 → 피트인), 피트 리미터 경고,
    /// 블루 플래그, 페널티(게임 메시지에서 종류/사유 파싱), 클래스 순위 변동.
    /// 내 레이스가 끝나면(RaceOver) 블루 플래그·페널티·순위 변동은 끄고 옐로·리미터만 남긴다.
    /// </summary>
    public sealed class RaceControlAnalyzer
    {
        private const int PhaseGreen = 5;
        private const int PhaseFcy = 6;
        private const int PhaseOver = 8;
        private const int YPitClosed = 2;
        private const int YPitLeadLap = 3;
        private const int YPitOpen = 4;
        private const int YFcyMin = 1;              // mYellowFlagState 풀코스 하위 상태 1..7
        private const int YFcyMax = 7;
        private const int SectorFlagYellow = 1;     // mSectorFlag[i] = 섹터 i+1, 원시값 1만 로컬 옐로
        private const double SectorYellowRearmSec = 120.0;  // 같은 섹터 재콜 최소 간격

        internal static readonly int[] MilestonesMin = { 60, 30, 10 };
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
        private bool _postLanePrev;            // 직전 틱 피트레인 여부 (체커 뒤 피트인 엣지)
        private bool _postGarage;              // 직전 틱 차고 여부 (체커 뒤 피트인 콜 무효화)

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
            _classPlace = null;
            _limiterWarnedT = 0.0;
            _blueFlag = false;
            _penalties = null;
            _recentMsgs.Clear();
            _lastStatusMsg = "";
            _lastHistoryMsg = "";
            _penDue = null;
            _penCount = 0;
            _postLanePrev = false;
            _postGarage = false;
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

            // 내 피니시(또는 DNF) 뒤엔 블루 플래그·페널티를 말하지 않는다 (상태 추적은 계속).
            // 옐로와 피트 리미터는 쿨다운 랩·피트인에서도 안전 콜이다
            bool over = state.RaceOverNow(me);
            CheckSectorYellow(ses, snap.T, bus);
            CheckPitLimiter(ses, snap, bus);
            CheckBlueFlag(me, state, bus, over);
            CollectMessages(ses, snap.T);
            CheckPenalties(me, snap.T, state, bus, over);
            if (state.IsRace && phase == PhaseGreen)
                CheckTimeMilestones(ses, bus);
            if (state.IsRace)
            {
                CheckRaceEndTick(state, snap, me, bus);
                CheckPostRace(state, snap, me, bus);
            }
        }

        private void OnPhaseChange(int old, int now, RaceState state, EventBus bus)
        {
            // 레이스 스타트: 포메이션/카운트다운 → 그린
            if (now == PhaseGreen && (old == 3 || old == 4) && state.IsRace)
            {
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
            // 체커 게시 — 페이즈 8은 리더 피니시라 랩 다운·다른 클래스 차는 아직 달린다.
            // 내 체커 콜은 내 피니시(PushRaceEnd)에서
            else if (now == PhaseOver && state.IsRace)
            {
                state.AddNarrative("(이벤트) 체커 게시 (리더 피니시)");
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

        /// <summary>
        /// 풀코스옐로 진행 중 — 페이즈 6, 또는 FCY 하위 상태(mYellowFlagState 1..7).
        /// LMU는 페이즈가 그린으로 돌아온 뒤에도 리스타트 준비(6)를 잠깐 남긴다 (CrewChief LMU 매퍼가
        /// 이 경우도 FCY로 친다). 상한 7은 부호 없는 바이트로 읽은 -1(=255, 무효)을 FCY로 세지 않게.
        /// racecontrol.fcy_active.
        /// </summary>
        internal static bool FcyActive(int phase, int yellow)
        {
            return phase == PhaseFcy || (yellow >= YFcyMin && yellow <= YFcyMax);
        }

        private void CheckSectorYellow(SessionInfo ses, double now, EventBus bus)
        {
            // 섹터 로컬 옐로 — 엣지 트리거: 섹터 값이 '옐로 아님 → 옐로'로 바뀌는 순간만 콜한다.
            // mSectorFlag[i]는 섹터 i+1이고 원시값 1이 그 섹터의 로컬 옐로다. 깨끗한 섹터는 LMU가 11,
            // rF2 헤더 기준 0 — LMU의 깨끗함 11은 lmu-pitwall 인게임 측정(LMU 1.4)과 LmuOverlay 검증
            // 목록에 있고, CrewChief·TinyPedal·SimHub·pitwall 모두 '== 1'만 옐로로 본다. 그래서 1 말고는
            // 전부 옐로 아님이다 (0·11·2·7·255 등 — 2를 옐로로 본 자료는 확인 안 된 포럼 글뿐). 예전
            // '0 이하 → 1..2' 규칙은 LMU의 11 → 1을 못 잡아 콜이 한 번도 안 나갔다. '== 1' 비교라
            // 파이썬 int와 C# byte, 부호 있는 -1과 부호 없는 255가 결과를 가르지 않는다.
            // 첫 샘플(리셋 직후)은 기록만 한다 — 붙기 전부터 켜져 있던 옐로는 언제 났는지 몰라 부르지 않는다.
            // FCY 중(FcyActive)에는 콜하지 않는다: FCY·피트 오픈·그린 콜이 따로 있고, LMU가 FCY 동안
            // 섹터 값을 어떻게 쓰는지 측정한 자료가 없다 (전 섹터를 1로 올리면 FCY 콜에 섹터 콜이 겹친다 —
            // CrewChief는 그린 페이즈에서만, LmuOverlay는 FCY 배너가 섹터 옐로보다 먼저).
            // 직전 값은 억제 중에도 매 틱 갱신한다 — FCY 동안 1이 된 섹터가 FCY가 끝나는 순간 가짜
            // 엣지로 나가지 않게. racecontrol._check_sector_yellow.
            if (!_sectorCalls)
                return;
            byte[] flags = ses.SectorFlags ?? new byte[0];
            byte[] prev = _prevSectorFlags;
            int n = Math.Min(flags.Length, 3);
            var cur = new byte[n];
            Array.Copy(flags, cur, n);
            _prevSectorFlags = cur;
            if (prev == null)
                return;                                 // 첫 샘플: 이미 켜져 있던 플래그는 신뢰하지 않음
            if (FcyActive(ses.GamePhase, ses.YellowState))
                return;                                 // FCY는 FCY 콜이 맡는다 (직전 값은 위에서 이미 갱신)
            for (int i = 0; i < n; i++)
            {
                bool was = i < prev.Length && prev[i] == SectorFlagYellow;
                bool flag = cur[i] == SectorFlagYellow;
                if (was || !flag)
                    continue;                           // 옐로 아님 → 옐로(1) 전이만
                double last;
                if (_sectorYellowAnnounced.TryGetValue(i, out last)
                    && now - last < SectorYellowRearmSec)
                    continue;
                _sectorYellowAnnounced[i] = now;
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.SectorYellow,
                    Priority = Priority.High,
                    Message = Messages.Get("sector_yellow", "n", i + 1),
                    DedupKey = "syellow_" + i,
                    Ttl = 15.0,
                    Tone = "urgent",
                });
            }
        }

        private void CheckBlueFlag(VehicleInfo me, RaceState state, EventBus bus, bool over)
        {
            // 내게 블루 플래그가 게시된 순간(mFlag=6 엣지) 양보 안내
            bool blue = me.FlagBlue;
            if (blue && !_blueFlag && !over)
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
                                    EventBus bus, bool over)
        {
            int n = me.NumPenalties;
            if (over)
            {
                // 피니시 뒤엔 소화할 수도 없다 — 기준만 따라가고 대기 중인 콜도 버린다
                _penalties = n;
                _penDue = null;
                return;
            }
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
                    Message = Messages.Get("pen_clear"),
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
                message = Messages.PenaltyText(kind, reason);
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

        private void CheckTimeMilestones(SessionInfo ses, EventBus bus)
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
        }

        // -- 마지막 랩 / 내 피니시 (판단은 state.RaceEnd) ------------------------

        private void CheckRaceEndTick(RaceState state, Snapshot snap, VehicleInfo me,
                                      EventBus bus)
        {
            // state.Update 전에 돈다(RaceEnd는 직전 틱 값). 이번 틱에 라인을 넘었으면 OnLap이 판단한다.
            RaceEndTracker re = state.RaceEnd;
            if (!re.Lap.HasValue || me.TotalLaps != re.Lap.Value || re.Out)
                return;
            int fs = me.FinishStatus;
            if (fs == 1 || re.Finished)
            {
                PushRaceEnd(state, snap, me, bus);   // 피니시 플래그가 크로싱 없이 늦게 켜진 경우 등
                return;
            }
            if (fs != 0 || re.FinalCalledFor == me.TotalLaps)
                return;
            SessionInfo ses = snap.Session;
            if (!RaceEnd.IsRacingPhase(ses.GamePhase))
                return;
            if (!RaceEnd.FlagOut(snap))
            {
                // 내가 리더면 체커는 내 통과에서야 나온다 — 시계가 끝났으면 이번 랩이 마지막
                if (!(0 < ses.EndEt && ses.EndEt <= ses.CurrentEt))
                    return;
                VehicleInfo leader = RaceEnd.RaceLeader(snap);
                if (leader == null || leader.Id != me.Id)
                    return;
            }
            // 예측이 빗나간 채 리더가 체커를 받았다(또는 내가 리더인데 시계가 끝났다) — 다음 라인 통과가 피니시.
            // 라인까지 몇 초 안 남았으면 '마지막 랩'과 체커 콜이 겹치니 생략
            double? pace = state.BaselineLapTime();
            if (!pace.HasValue || pace.Value <= 0)
                pace = RaceEnd.LapPace(me);
            if (pace.HasValue && pace.Value > 0
                && (1.0 - RaceEnd.LapFrac(me, ses.CurrentEt, ses.TrackLength, pace.Value))
                   * pace.Value < RaceEnd.LateMinToLineSec)
            {
                return;
            }
            PushFinalLap(state, snap, me, bus);
        }

        private void PushFinalLap(RaceState state, Snapshot snap, VehicleInfo me, EventBus bus)
        {
            int lap = me.TotalLaps;
            RaceEndTracker re = state.RaceEnd;
            int gen = re.Generation;
            // 클래스 선두면 선두 변형 풀 ("Final lap, P1. Nothing silly now.")
            string pool = RaceState.ClassPlaceOf(snap, me) == 1 ? "final_lap_lead" : "final_lap";
            // 키는 랩마다 — 예측이 빗나가 다음 랩에 다시 부를 때 앞 콜 키에 막히지 않게.
            // 채널이 밀려 라인을 넘은 뒤에 나가면 안 된다 (안전망 콜은 라인 몇 초 전일 수 있다)
            var ev = new RadioEvent
            {
                Type = EventTypes.RaceMilestone,
                Priority = Priority.High,
                DedupKey = "final_lap_" + (lap + 1),
                Ttl = 30.0,
                ValidFn = () => re.Generation == gen && !re.Finished && re.Lap == lap,
            };
            ev.Data["final_lap"] = true;
            ev.Data["lap"] = lap + 1;
            ev.Data["pool"] = pool;
            if (bus.Push(ev))
            {
                re.FinalCalledFor = lap;         // 수락됐을 때만 — 거절되면 안전망이 다시 시도
                state.AddNarrative("(이벤트) 마지막 랩");
            }
        }

        private void PushRaceEnd(RaceState state, Snapshot snap, VehicleInfo me, EventBus bus)
        {
            RaceEndTracker re = state.RaceEnd;
            if (re.EndCalled || !re.SeenUnfinished)
                return;
            int place = RaceState.ClassPlaceOf(snap, me);
            int start, gained;
            string pool = RaceEnd.FinishCall(place, re.StartClassPlace ?? 0,
                RaceEnd.IsMulticlass(snap), out start, out gained);
            var ev = new RadioEvent
            {
                Type = EventTypes.RaceEnd,
                Priority = Priority.High,
                Ttl = 30.0,
            };
            ev.Data["pool"] = pool;
            ev.Data["class_place"] = place;
            ev.Data["start_place"] = start;
            ev.Data["gained"] = gained;
            if (bus.Push(ev))
            {
                re.EndCalled = true;
                re.EndT = snap.T;
                state.AddNarrative("(이벤트) 체커, 클래스 P" + place + " (" + pool + ", 스타트 P"
                                   + (start != 0 ? start.ToString() : "?") + ")");
            }
        }

        // -- 체커 뒤 무전: 쿨다운 랩 → 요약 → 피트인 (racecontrol._check_post_race) ------

        private void CheckPostRace(RaceState state, Snapshot snap, VehicleInfo me, EventBus bus)
        {
            PlayerInfo p = snap.Player;
            bool inLane = (p != null && p.InPitLane) || me.InPits;
            bool entered = inLane && !_postLanePrev;
            _postLanePrev = inLane;
            _postGarage = me.InGarage;
            RaceEndTracker re = state.RaceEnd;
            if (!re.EndCalled || !re.EndT.HasValue)
                return;
            double since = snap.T - re.EndT.Value;
            int gen = re.Generation;
            if (entered && !re.PitInCalled && _postGarage)
            {
                // ESC로 차고에 순간이동 (InPits·InGarage 같은 틱) — "리미터 켜고 차고로"는
                // 이미 세워 둔 차에 틀린 말이라 말없이 래치 (쿨다운 콜도 늦었다)
                re.PitInCalled = true;
                re.CooldownCalled = true;
                state.AddNarrative("(이벤트) 체커 뒤 차고로 바로 복귀 — 피트인 콜 생략");
            }
            else if (entered && !re.PitInCalled)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.PostRacePit,
                    Priority = Priority.High,
                    Ttl = 15.0,
                    // 대기 중에 차고로 들어가면(순간이동) 버린다
                    ValidFn = () => re.Generation == gen && _postLanePrev && !_postGarage,
                };
                ev.Data["pool"] = "post_race_pit";
                if (bus.Push(ev))
                {
                    re.PitInCalled = true;
                    re.CooldownCalled = true;     // 이미 들어오는 중 — 쿨다운 콜은 늦었다
                    state.AddNarrative("(이벤트) 체커 뒤 피트인");
                }
            }
            if (!re.CooldownCalled && since >= RaceEnd.PostCooldownSec - RaceEnd.PostEps)
            {
                if (inLane || me.InGarage)
                {
                    re.CooldownCalled = true;
                }
                else
                {
                    // 빠른 클래스 = 트래픽과 같은 판정 (계열 서열, 모르면 관측 페이스, 그것도 없으면 아님)
                    Dictionary<string, double> pace = VehicleClass.Pace(snap.Vehicles);
                    int racing = 0;
                    bool faster = false;
                    foreach (VehicleInfo v in snap.Vehicles)
                    {
                        if (v.Id == me.Id || v.FinishStatus != 0 || v.Place <= 0
                            || v.InPits || v.InGarage)
                            continue;
                        racing++;
                        if (VehicleClass.Relation(me.Class, v.Class, pace) > 0)
                            faster = true;
                    }
                    string pool = RaceEnd.CooldownPool(racing, faster);
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.CooldownLap,
                        Priority = Priority.High,
                        Ttl = 25.0,
                        ValidFn = () => re.Generation == gen && !_postLanePrev,
                    };
                    ev.Data["pool"] = pool;
                    ev.Data["still_racing"] = racing;
                    if (bus.Push(ev))
                    {
                        re.CooldownCalled = true;
                        state.AddNarrative("(이벤트) 쿨다운 랩 (" + pool + ")");
                    }
                }
            }
            if (re.CooldownCalled && !re.SummaryCalled
                && since >= RaceEnd.PostSummarySec - RaceEnd.PostEps)
            {
                int timed;
                int rank = RaceEnd.BestLapRank(snap, me, out timed);
                string pool = RaceEnd.SummaryPool(rank, timed, RaceEnd.IsMulticlass(snap));
                var ev = new RadioEvent
                {
                    Type = EventTypes.RaceSummary,
                    Priority = Priority.Normal,
                    Ttl = 45.0,
                    ValidFn = () => re.Generation == gen,
                };
                ev.Data["pool"] = pool;
                ev.Data["rank"] = rank;
                if (bus.Push(ev))
                {
                    re.SummaryCalled = true;
                    state.AddNarrative("(이벤트) 레이스 요약 (" + pool + ")");
                }
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
            RaceEndTracker re = state.RaceEnd;
            if (state.RaceOver)
            {
                if (re.Finished)
                    PushRaceEnd(state, snap, me, bus);
                return;                      // 피니시 라인(과 그 뒤): 순위 변동 콜 대신 체커 콜 하나
            }
            if (re.FinalLap && re.FinalCalledFor != me.TotalLaps)
                PushFinalLap(state, snap, me, bus);
            if (!prev.HasValue || cp == prev.Value || cp > SpeechDomain.ClassPosMax)
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
