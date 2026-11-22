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
    /// 처음 보는 차는 기준값만, 앞/뒤는 진입 전 PlaceHistS초 순위 기록으로(그 안에서 한 번이라도
    /// 앞이었으면 앞차), 직전·진입 틱 순위 0·동률·차고 순간이동은 침묵, "언더컷"(뒤차)은 그린 +
    /// 상대 1랩 이상 + 트랙 위 1/4랩 안쪽 뒤 + 창 내내 뒤 + 우리가 피트·아웃랩이 아닐 때만.
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
        private const double PlaceHistS = 5.0;         // 앞/뒤 판정 창 — 진입 전 순위 기록 (5Hz ≈ 25틱)

        /// <summary>한 차의 한 틱 — 클래스 순위(없으면 0)와 트랙 위 누적 진행(랩).</summary>
        private struct PlaceSample
        {
            public int Cp;
            public double Prog;
        }

        /// <summary>rivals._hist의 한 항목 — 한 틱의 나와 동클래스 경쟁자 순위.</summary>
        private sealed class TickRecord
        {
            public double T;
            public PlaceSample Me;
            public bool MyInPits;     // 내가 피트/차고
            public readonly Dictionary<int, PlaceSample> Cars = new Dictionary<int, PlaceSample>();
        }

        private readonly double _paceDiffMin;
        private readonly Dictionary<int, bool> _inPits = new Dictionary<int, bool>();
        // 틱 기록 (오래된 → 최근) — PlaceHistS초를 다 덮는 만큼만, 창보다 오래된 것은 가장 최근 하나만
        private readonly List<TickRecord> _hist = new List<TickRecord>();
        private int? _myPitLap;      // 내가 마지막으로 피트에 있던 틱의 완료 랩
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
            _hist.Clear();
            _myPitLap = null;
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
            double t = snap.T;
            // 틱이 끊겼으면(모니터·일시정지·재시작) 그사이 바뀐 플래그를 진입으로 보지 않는다
            if (_lastT.HasValue && !(t - _lastT.Value >= 0.0 && t - _lastT.Value <= ReseedGapS))
                ClearPitTracking();
            _lastT = t;
            while (_hist.Count >= 2 && t - _hist[1].T >= PlaceHistS)
                _hist.RemoveAt(0);

            var myNow = new PlaceSample
            {
                Cp = me.Place > 0 ? RaceState.ClassPlaceOf(snap, me) : 0,
                Prog = Progress(me, tl),
            };
            bool myIn = me.InPits || me.InGarage;
            if (myIn)
                _myPitLap = me.TotalLaps;
            // 우리가 피트에 있거나, 창 안에 있었거나, 아웃랩 중 — 뒤차 피트는 언더컷이 아니다
            bool myPitting = myIn || (_myPitLap.HasValue && me.TotalLaps <= _myPitLap.Value);
            foreach (TickRecord r in _hist)
            {
                if (r.MyInPits)
                    myPitting = true;
            }

            var rec = new TickRecord { T = t, Me = myNow, MyInPits = myIn };
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.IsPlayer || v.Class != me.Class || v.FinishStatus != 0)
                    continue;
                bool was;
                bool known = _inPits.TryGetValue(v.Id, out was);   // 처음 봄 → 기준값만
                bool nowIn = v.InPits;
                _inPits[v.Id] = nowIn;
                var cur = new PlaceSample
                {
                    Cp = v.Place > 0 ? RaceState.ClassPlaceOf(snap, v) : 0,
                    Prog = Progress(v, tl),
                };
                rec.Cars[v.Id] = cur;
                if (!known || was || !nowIn)
                    continue;    // 피트 진입 순간만

                bool hasPrev, hasMyPrev, hasBasis;
                PlaceSample prev, myPrev;
                int basisTheirs, basisMine;
                string why;
                Dictionary<string, object> data = JudgePit(t, phase, v, cur, myNow, myPitting,
                    out why, out hasPrev, out prev, out hasMyPrev, out myPrev,
                    out hasBasis, out basisTheirs, out basisMine);
                FileLog.Info(string.Format(CultureInfo.InvariantCulture,
                    "라이벌 피트 진입 {0}#{1} {2} · 페이즈 {3} · 직전 나 C{4} 상대 C{5} · "
                    + "기준 나 C{6} 상대 C{7} · 지금 나 P{8}/C{9} 상대 P{10}/C{11} · 랩 {12} {13:F0}m · "
                    + "차고 {14} · 피트 상태 {15} · 내 피트 {16}",
                    v.Driver, v.Id, data != null ? "콜" : "침묵(" + why + ")", phase,
                    hasMyPrev ? myPrev.Cp : -1, hasPrev ? prev.Cp : -1,
                    hasBasis ? basisMine : -1, hasBasis ? basisTheirs : -1,
                    me.Place, myNow.Cp, v.Place, cur.Cp,
                    v.TotalLaps, v.LapDist, v.InGarage, v.PitState, myPitting));
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
            _hist.Add(rec);
        }

        /// <summary>rivals._judge_pit — 피트 진입 한 건 판정. 콜 데이터(침묵이면 null과 사유),
        /// 직전 틱 기록, 앞/뒤 기준 (상대 클래스 순위, 내 클래스 순위). 기록은 진입 틱 이전 것만 본다.</summary>
        private Dictionary<string, object> JudgePit(double t, int phase, VehicleInfo v,
            PlaceSample cur, PlaceSample myNow, bool myPitting, out string why,
            out bool hasPrev, out PlaceSample prev, out bool hasMyPrev, out PlaceSample myPrev,
            out bool hasBasis, out int basisTheirs, out int basisMine)
        {
            prev = default(PlaceSample);
            myPrev = default(PlaceSample);
            hasPrev = false;
            hasMyPrev = false;
            hasBasis = false;
            basisTheirs = 0;
            basisMine = 0;
            if (_hist.Count > 0)
            {
                TickRecord last = _hist[_hist.Count - 1];
                hasPrev = last.Cars.TryGetValue(v.Id, out prev);
                hasMyPrev = true;
                myPrev = last.Me;
            }
            why = PitVeto(phase, v, hasPrev, prev.Cp, cur.Cp, hasMyPrev, myPrev.Cp, myNow.Cp);
            if (why != null)
                return null;
            // 창 안에서 가장 최근에 우리 앞이었던 순간 — 있으면 앞차, 없으면 직전 틱 순위.
            // 언더컷은 창을 다 덮는 기록이 있고 그 내내 (순위 0·동률 없이) 우리 뒤였을 때만
            bool ahead = false;
            bool behindAll = t - _hist[0].T >= PlaceHistS;
            for (int i = _hist.Count - 1; i >= 0; i--)
            {
                TickRecord r = _hist[i];
                PlaceSample s;
                bool known = r.Cars.TryGetValue(v.Id, out s) && s.Cp > 0 && r.Me.Cp > 0;
                if (known && s.Cp < r.Me.Cp)
                {
                    ahead = true;
                    basisTheirs = s.Cp;
                    basisMine = r.Me.Cp;
                    break;
                }
                if (!(known && s.Cp > r.Me.Cp))
                    behindAll = false;
            }
            if (!ahead)
            {
                basisTheirs = prev.Cp;
                basisMine = myPrev.Cp;
            }
            hasBasis = true;
            Dictionary<string, object> data = PitData(basisTheirs, basisMine, v.Driver);
            if (data == null)
            {
                why = "range";
                return null;
            }
            if ((bool)data["undercut_risk"])
            {
                if (myPitting)
                    why = "my-pit";
                else if (!behindAll)
                    why = "window";
                else if (!(phase == PhaseGreen && prev.Prog >= 1.0
                           && myPrev.Prog - prev.Prog > 0.0
                           && myPrev.Prog - prev.Prog < UndercutMaxLaps))
                    why = "not-undercut";
                if (why != null)
                    return null;
            }
            return data;
        }

        /// <summary>rivals._pit_veto — 피트 진입 콜을 막는 사유 (없으면 null) —
        /// 페이즈, 차고, 직전 틱 기록, 순위 0.</summary>
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
