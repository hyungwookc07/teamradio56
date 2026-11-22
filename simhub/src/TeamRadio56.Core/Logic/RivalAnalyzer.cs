using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/rivals.py 포팅 — 동클래스 경쟁자 관찰.
    /// 경쟁자 피트 진입(언더컷/오버컷 재료)과 페이스 비교 인텔.
    /// 피트 플래그는 레이스 틱마다(페이즈 무관) 추적하고 콜만 거른다 — 그린/FCY에서만,
    /// 처음 보는 차는 기준값만, 앞/뒤는 진입 직전 틱 순위로, 순위 0·동률·차고 순간이동은
    /// 침묵, "언더컷"(뒤차)은 그린 + 상대 1랩 이상 + 트랙 위 1/4랩 안쪽 뒤일 때만.
    /// </summary>
    public sealed class RivalAnalyzer
    {
        private const int PitRelevantRange = 2;
        private const double CatchableLaps = 15.0;
        // rF2 mGamePhase — 0 세션 전, 1 정찰, 2 그리드 워크, 3 포메이션, 4 카운트다운,
        // 5 그린, 6 FCY/SC, 7 중단, 8 종료, 9 일시정지
        private const int PhaseGreen = 5;
        private const int PhaseFcy = 6;
        private const double UndercutMaxLaps = 0.25;   // 트랙 위에서 이 랩 비율 안쪽 뒤
        private const double ReseedGapS = 2.0;         // 틱 공백이 이보다 길면 기준값 다시

        private readonly double _paceDiffMin;
        private readonly Dictionary<int, bool> _inPits = new Dictionary<int, bool>();
        private readonly Dictionary<int, int> _prevCp = new Dictionary<int, int>();       // 직전 틱 클래스 순위|0
        private readonly Dictionary<int, double> _prevProg = new Dictionary<int, double>(); // 직전 틱 진행 (랩)
        private bool _hasPrevMe;
        private int _prevMyCp;
        private double _prevMyProg;
        private double? _lastT;
        private readonly Dictionary<int, List<double>> _lapsSeen =
            new Dictionary<int, List<double>>();

        public RivalAnalyzer(double rivalPaceDiff = 0.3)
        {
            _paceDiffMin = rivalPaceDiff;
        }

        public void Reset()
        {
            ClearPitTracking();
            _lastT = null;
            _lapsSeen.Clear();
        }

        private void ClearPitTracking()
        {
            _inPits.Clear();
            _prevCp.Clear();
            _prevProg.Clear();
            _hasPrevMe = false;
            _prevMyCp = 0;
            _prevMyProg = 0.0;
        }

        /// <summary>rivals.pit_data — 경쟁자 피트 콜 데이터. 클래스 순위 ±2 밖이거나
        /// 무의미한 순위면 null. 퍼즈가 같은 함수를 부른다.</summary>
        public static Dictionary<string, object> PitData(int theirCp, int myCp, string driver = "")
        {
            if (theirCp <= 0 || myCp <= 0 || theirCp == myCp)
                return null;     // 순위가 없거나 같으면 앞/뒤를 말할 수 없다
            if (Math.Abs(theirCp - myCp) > PitRelevantRange || !SpeechDomain.RivalPitOk(theirCp))
                return null;
            return new Dictionary<string, object>
            {
                { "driver", driver },
                { "their_class_place", theirCp },
                { "rel", theirCp < myCp ? "앞" : "뒤" },
                { "undercut_risk", theirCp > myCp },   // 뒤차가 먼저 피트 → 언더컷 위협
            };
        }

        /// <summary>rivals.pace_data — 페이스 비교 콜 데이터. diff = 상대 평균 - 우리 평균
        /// (+면 우리가 빠름). 15랩 안에 잡거나 잡힐 때만, 랩당 5초 넘는 차이(피트/스핀 랩이
        /// 섞인 평균)는 null. 퍼즈가 같은 함수를 부른다.</summary>
        public static Dictionary<string, object> PaceData(double diff, double gap, bool ahead,
                                                          string driver = "")
        {
            Dictionary<string, object> data;
            if (ahead && diff > 0)                        // 앞차가 더 느림 → 추격
            {
                double lapsToCatch = gap / diff;
                if (lapsToCatch > CatchableLaps)
                    return null;
                data = new Dictionary<string, object>
                {
                    { "mode", "catch" },
                    { "driver", driver },
                    { "diff", Math.Round(diff, 2) },
                    { "laps", Math.Max((int)lapsToCatch + 1, 1) },
                    { "gap", Math.Round(gap, 1) },
                };
            }
            else if (!ahead && diff < 0)                  // 뒤차가 더 빠름 → 방어
            {
                double lapsToCaught = gap / -diff;
                if (lapsToCaught > CatchableLaps)
                    return null;
                data = new Dictionary<string, object>
                {
                    { "mode", "defend" },
                    { "driver", driver },
                    { "diff", Math.Round(-diff, 2) },
                    { "laps", Math.Max((int)lapsToCaught + 1, 1) },
                    { "gap", Math.Round(gap, 1) },
                };
            }
            else
            {
                return null;
            }
            return SpeechDomain.RivalDiffOk((double)data["diff"]) ? data : null;
        }

        // -- 5Hz: 경쟁자 피트 진입 --------------------------------------------

        public void OnTick(RaceState state, Snapshot snap, EventBus bus)
        {
            if (!state.IsRace)
                return;
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return;
            int phase = snap.Session.GamePhase;
            double tl = snap.Session.TrackLength;
            // 틱이 끊겼으면(모니터·일시정지·재시작) 그사이 바뀐 플래그를 진입으로 보지 않는다
            if (_lastT.HasValue && !(snap.T - _lastT.Value >= 0.0 && snap.T - _lastT.Value <= ReseedGapS))
                ClearPitTracking();
            _lastT = snap.T;

            int myCpNow = me.Place > 0 ? RaceState.ClassPlaceOf(snap, me) : 0;
            double myProgNow = Progress(me, tl);
            bool hasMyPrev = _hasPrevMe;
            int myCpPrev = _prevMyCp;
            double myProgPrev = _prevMyProg;
            _hasPrevMe = true;
            _prevMyCp = myCpNow;
            _prevMyProg = myProgNow;

            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer || v.Class != me.Class || v.FinishStatus != 0)
                    continue;
                bool was;
                bool known = _inPits.TryGetValue(v.Id, out was);   // 처음 봄 → 기준값만
                bool nowIn = v.InPits;
                _inPits[v.Id] = nowIn;
                int theirCpPrev;
                bool hasPrev = _prevCp.TryGetValue(v.Id, out theirCpPrev);
                double theirProgPrev;
                _prevProg.TryGetValue(v.Id, out theirProgPrev);
                int theirCpNow = v.Place > 0 ? RaceState.ClassPlaceOf(snap, v) : 0;
                _prevCp[v.Id] = theirCpNow;
                _prevProg[v.Id] = Progress(v, tl);
                if (!known || was || !nowIn)
                    continue;    // 피트 진입 순간만

                string why = PitVeto(phase, v, hasPrev, theirCpPrev, theirCpNow,
                                     hasMyPrev, myCpPrev, myCpNow);
                Dictionary<string, object> data = null;
                if (why == null)
                {
                    data = PitData(theirCpPrev, myCpPrev, v.Driver);
                    if (data == null)
                    {
                        why = "range";
                    }
                    else if ((bool)data["undercut_risk"]
                             && !(phase == PhaseGreen && theirProgPrev >= 1.0
                                  && myProgPrev - theirProgPrev > 0.0
                                  && myProgPrev - theirProgPrev < UndercutMaxLaps))
                    {
                        data = null;
                        why = "not-undercut";
                    }
                }
                FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "라이벌 피트 진입 {0}#{1} {2} · 페이즈 {3} · 직전 나 C{4} 상대 C{5} · "
                    + "지금 나 P{6}/C{7} 상대 P{8}/C{9} · 랩 {10} {11:F0}m · 차고 {12} · 피트 상태 {13}",
                    v.Driver, v.Id, data != null ? "콜" : "침묵(" + why + ")", phase,
                    hasMyPrev ? myCpPrev : -1, hasPrev ? theirCpPrev : -1,
                    me.Place, myCpNow, v.Place, theirCpNow,
                    v.TotalLaps, v.LapDist, v.InGarage, v.PitState));
                if (data == null)
                    continue;
                string rel = (string)data["rel"];
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.RivalPit,
                    Priority = Priority.Normal,
                    Data = data,
                    DedupKey = "rpit_" + v.Id + "_" + v.NumPitstops,
                    Ttl = 25.0,
                });
                state.AddNarrative("(이벤트) 클래스 " + rel + " P" + data["their_class_place"] + " "
                                   + v.Driver + " 피트 인");
            }
        }

        /// <summary>rivals._pit_veto — 피트 진입 콜을 막는 사유 (없으면 null).
        /// 앞/뒤는 진입 직전 틱 순위로 정한다.</summary>
        private static string PitVeto(int phase, VehicleInfo v, bool hasPrev, int theirCpPrev,
                                      int theirCpNow, bool hasMyPrev, int myCpPrev, int myCpNow)
        {
            if (phase != PhaseGreen && phase != PhaseFcy)
                return "phase";
            if (v.InGarage)
                return "garage";
            if (!hasPrev || !hasMyPrev)
                return "no-prev";
            if (theirCpPrev <= 0 || myCpPrev <= 0 || theirCpNow <= 0 || myCpNow <= 0)
                return "place";
            return null;
        }

        /// <summary>rivals._progress — 트랙 위 누적 진행 (랩) = 완료 랩 + 이번 랩 비율.</summary>
        private static double Progress(VehicleInfo v, double trackLen)
        {
            return v.TotalLaps + (trackLen > 0 ? v.LapDist / trackLen : 0.0);
        }

        // -- 랩 완료: 페이스 비교 인텔 -----------------------------------------

        public void OnLap(RaceState state, Snapshot snap, EventBus bus)
        {
            if (!state.IsRace)
                return;
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return;
            RecordRivalLaps(snap, me);

            var myTimes = new List<double>();
            foreach (LapRecord l in state.Laps)
            {
                if (l.Valid)
                    myTimes.Add(l.LapTime);
            }
            if (myTimes.Count > 3)
                myTimes = myTimes.GetRange(myTimes.Count - 3, 3);
            double? myAvg = Avg(myTimes);
            if (!myAvg.HasValue || state.Laps.Count < 5)
                return;
            int myCp = RaceState.ClassPlaceOf(snap, me);

            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer || v.Class != me.Class || v.InPits || v.FinishStatus != 0)
                    continue;
                int theirCp = RaceState.ClassPlaceOf(snap, v);
                if (Math.Abs(theirCp - myCp) != 1)
                    continue;    // 클래스 바로 앞/뒤만
                List<double> seen;
                double? theirAvg = _lapsSeen.TryGetValue(v.Id, out seen)
                    ? Avg(seen) : null;
                if (!theirAvg.HasValue)
                    continue;
                double diff = theirAvg.Value - myAvg.Value;   // +면 우리가 빠름
                if (Math.Abs(diff) < _paceDiffMin)
                    continue;

                bool ahead = theirCp < myCp;
                LapRecord lastLap = state.Laps[state.Laps.Count - 1];
                double gap = ahead ? lastLap.GapAhead : lastLap.GapBehind;
                if (gap < 0)
                    continue;
                Dictionary<string, object> data = PaceData(diff, gap, ahead, v.Driver);
                if (data == null)
                    continue;
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.RivalPace,
                    Priority = Priority.Normal,
                    Data = data,
                    DedupKey = "rpace_" + v.Id + "_" + data["mode"],
                });
                break;    // 한 랩에 한 건만
            }
        }

        private void RecordRivalLaps(Snapshot snap, VehicleInfo me)
        {
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer || v.Class != me.Class)
                    continue;
                double t = v.LastLap;
                if (t <= 0)
                    continue;
                List<double> dq;
                if (!_lapsSeen.TryGetValue(v.Id, out dq))
                {
                    dq = new List<double>();
                    _lapsSeen[v.Id] = dq;
                }
                if (dq.Count == 0 || Math.Abs(dq[dq.Count - 1] - t) > 1e-3)
                {
                    dq.Add(t);
                    while (dq.Count > 3)      // deque(maxlen=3)
                        dq.RemoveAt(0);
                }
            }
        }

        private static double? Avg(List<double> times)
        {
            var pool = new List<double>();
            if (times != null)
            {
                foreach (double t in times)
                {
                    if (t > 0)
                        pool.Add(t);
                }
            }
            if (pool.Count < 2)
                return null;
            double sum = 0;
            foreach (double t in pool)
                sum += t;
            return sum / pool.Count;
        }
    }
}
