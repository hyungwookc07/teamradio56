using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/rivals.py 포팅 — 동클래스 경쟁자 관찰.
    /// 경쟁자 피트 진입(언더컷/오버컷 재료)과 페이스 비교 인텔.
    /// </summary>
    public sealed class RivalAnalyzer
    {
        private const int PitRelevantRange = 2;
        private const double CatchableLaps = 15.0;

        private readonly double _paceDiffMin;
        private readonly Dictionary<int, bool> _inPits = new Dictionary<int, bool>();
        private readonly Dictionary<int, List<double>> _lapsSeen =
            new Dictionary<int, List<double>>();

        public RivalAnalyzer(double rivalPaceDiff = 0.3)
        {
            _paceDiffMin = rivalPaceDiff;
        }

        public void Reset()
        {
            _inPits.Clear();
            _lapsSeen.Clear();
        }

        // -- 5Hz: 경쟁자 피트 진입 --------------------------------------------

        public void OnTick(RaceState state, Snapshot snap, EventBus bus)
        {
            if (!state.IsRace)
                return;
            VehicleInfo me = snap.PlayerScoring();
            if (me == null)
                return;
            int myCp = RaceState.ClassPlaceOf(snap, me);

            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer || v.Class != me.Class || v.FinishStatus != 0)
                    continue;
                bool was;
                _inPits.TryGetValue(v.Id, out was);
                bool nowIn = v.InPits;
                _inPits[v.Id] = nowIn;
                if (!(nowIn && !was))
                    continue;    // 피트 진입 순간만
                int theirCp = RaceState.ClassPlaceOf(snap, v);
                if (Math.Abs(theirCp - myCp) > PitRelevantRange)
                    continue;
                string rel = theirCp < myCp ? "앞" : "뒤";
                bool undercutRisk = theirCp > myCp;   // 뒤차가 먼저 피트 → 언더컷 위협
                var ev = new RadioEvent
                {
                    Type = EventTypes.RivalPit,
                    Priority = Priority.Normal,
                    DedupKey = "rpit_" + v.Id + "_" + v.NumPitstops,
                    Ttl = 25.0,
                };
                ev.Data["driver"] = v.Driver;
                ev.Data["their_class_place"] = theirCp;
                ev.Data["rel"] = rel;
                ev.Data["undercut_risk"] = undercutRisk;
                bus.Push(ev);
                state.AddNarrative("(이벤트) 클래스 " + rel + " P" + theirCp + " "
                                   + v.Driver + " 피트 인");
            }
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
                Dictionary<string, object> data;
                if (ahead && diff > 0)                        // 앞차가 더 느림 → 추격
                {
                    double lapsToCatch = gap / diff;
                    if (lapsToCatch > CatchableLaps)
                        continue;
                    data = new Dictionary<string, object>
                    {
                        { "mode", "catch" },
                        { "driver", v.Driver },
                        { "diff", Math.Round(diff, 2) },
                        { "laps", Math.Max((int)lapsToCatch + 1, 1) },
                        { "gap", Math.Round(gap, 1) },
                    };
                }
                else if (!ahead && diff < 0)                  // 뒤차가 더 빠름 → 방어
                {
                    double lapsToCaught = gap / -diff;
                    if (lapsToCaught > CatchableLaps)
                        continue;
                    data = new Dictionary<string, object>
                    {
                        { "mode", "defend" },
                        { "driver", v.Driver },
                        { "diff", Math.Round(-diff, 2) },
                        { "laps", Math.Max((int)lapsToCaught + 1, 1) },
                        { "gap", Math.Round(gap, 1) },
                    };
                }
                else
                {
                    continue;
                }
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
