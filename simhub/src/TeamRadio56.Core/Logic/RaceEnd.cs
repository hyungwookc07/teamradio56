using System;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// race_end.py 포팅 — 레이스 종료(체커) 판단: 마지막 랩/남은 랩/내 피니시.
    /// rF2/LMU: 시간제는 시계 0 이후 전체 1위(클래스 무관)가 라인을 넘을 때 체커, 나머지는 그 뒤
    /// 각자 첫 라인 통과에서 피니시. 랩제는 1위가 max_laps를 채울 때. 그래서 랩 다운·느린 클래스는
    /// 자기 페이스로 센 것보다 한 랩 더(또는 덜) 돈다. 페이즈 8은 '리더' 피니시다.
    /// </summary>
    public static class RaceEnd
    {
        public const int LapLimitMax = 100000;          // 시간제에선 max_laps에 INT_MAX가 들어온다
        public const double LeaderPaceMaxRatio = 1.2;   // 직전 랩이 베스트의 1.2배 초과(피트·FCY)면 베스트로
        public const double JustCrossedSec = 5.0;       // 랩 카운트는 넘어갔는데 lap_dist가 아직 안 감긴 순간
        public const double LateMinToLineSec = 10.0;    // 라인까지 이보다 가까우면 늦은 '마지막 랩'은 피니시와 겹친다
        public const int PhaseOver = 8;

        /// <summary>그린/FCY/체커 이후(리더만 끝남).</summary>
        public static bool IsRacingPhase(int p)
        {
            return p == 5 || p == 6 || p == 8;
        }

        public static double? LapPace(VehicleInfo v)
        {
            double last = v.LastLap, best = v.BestLap;
            if (last > 0 && (best <= 0 || last <= best * LeaderPaceMaxRatio))
                return last;
            if (best > 0)
                return best;
            return null;
        }

        public static double LapFrac(VehicleInfo v, double now, double trackLen, double pace)
        {
            double lse = v.LapStartEt;
            double f;
            if (trackLen > 0)
            {
                double d = v.LapDist;
                if (d < 0)
                    d += trackLen;
                f = d / trackLen;
                if (f > 0.5 && lse > 0 && 0 <= now - lse && now - lse < JustCrossedSec)
                    f = 0.0;
            }
            else if (lse > 0)
            {
                f = (now - lse) / pace;
            }
            else
            {
                f = 0.0;
            }
            return Math.Min(Math.Max(f, 0.0), 1.0);
        }

        public static bool FlagOut(Snapshot snap)
        {
            if (snap.Session.GamePhase == PhaseOver)
                return true;
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.FinishStatus == 1)
                    return true;
            }
            return false;
        }

        public static VehicleInfo RaceLeader(Snapshot snap)
        {
            VehicleInfo best = null;
            foreach (VehicleInfo v in snap.Vehicles)
            {
                if (v.Place <= 0 || v.FinishStatus != 0 || v.InGarage)
                    continue;      // DNF/DQ·차고 차는 체커를 받을 수 없다
                if (best == null || v.Place < best.Place
                    || (v.Place == best.Place && v.Id < best.Id))
                {
                    best = v;
                }
            }
            return best;
        }
    }

    /// <summary>race_end.py RaceEndTracker 포팅 — RaceState.Update 끝에서 매 틱 관측.</summary>
    public sealed class RaceEndTracker
    {
        public int? Lap;              // 마지막 Observe 때 내 total_laps (null=아직 관측 전)
        public int? LapsLeft;         // 마지막 라인 통과 기준 남은 랩(지금 랩 포함), 0=피니시
        public double? ChequeredEt;   // 리더 체커 시각 (처음 관측된 값)
        public bool Finished;
        public bool Out;              // 내 DNF/DQ
        public bool SeenUnfinished;
        // 콜 래치 — 트래커와 수명을 같이 해야 재시작(ET 되감기) 뒤 새 레이스에서 다시 부른다
        public int? FinalCalledFor;   // '마지막 랩'이 수락된 시점의 내 total_laps
        public bool EndCalled;
        public int Generation;        // Reset마다 증가 — 큐에 남은 콜이 이전 레이스 것인지 가린다

        public bool FinalLap
        {
            get { return LapsLeft == 1 && !Finished; }
        }

        public void Reset()
        {
            Lap = null;
            LapsLeft = null;
            ChequeredEt = null;
            Finished = false;
            Out = false;
            SeenUnfinished = false;
            FinalCalledFor = null;
            EndCalled = false;
            Generation++;
        }

        public void Observe(RaceState state, Snapshot snap, bool crossed)
        {
            VehicleInfo me = snap.PlayerScoring();
            if (me == null || !state.IsRace)
                return;
            SessionInfo ses = snap.Session;
            double now = ses.CurrentEt;
            int phase = ses.GamePhase;
            if (!ChequeredEt.HasValue && RaceEnd.FlagOut(snap))
            {
                // 체커 차의 lap_start_et가 실제 통과 시각 — 같은 틱에 넘은 차들의 선후를 가른다
                double? first = null;
                foreach (VehicleInfo v in snap.Vehicles)
                {
                    if (v.FinishStatus == 1 && v.LapStartEt > 0
                        && (!first.HasValue || v.LapStartEt < first.Value))
                    {
                        first = v.LapStartEt;
                    }
                }
                ChequeredEt = first ?? now;
            }
            Lap = me.TotalLaps;
            int fs = me.FinishStatus;
            if (fs >= 2)
            {
                Out = true;
                LapsLeft = null;
            }
            if (Finished || Out)
                return;
            if (fs == 0 && RaceEnd.IsRacingPhase(phase))
                SeenUnfinished = true;
            if (fs == 1)
            {
                Finished = true;
                LapsLeft = 0;
                return;
            }
            if (!crossed || !RaceEnd.IsRacingPhase(phase))
                return;
            double lse = me.LapStartEt;
            double tCross = lse > 0 && 0 <= now - lse && now - lse < RaceEnd.JustCrossedSec
                ? lse : now;
            LapsLeft = Estimate(state, snap, me, tCross);
            if (LapsLeft == 0)
                Finished = true;
        }

        private int? Estimate(RaceState state, Snapshot snap, VehicleInfo me, double tCross)
        {
            SessionInfo ses = snap.Session;
            double now = ses.CurrentEt, endEt = ses.EndEt, trackLen = ses.TrackLength;
            int maxLaps = ses.MaxLaps;
            bool timed = endEt > 0;
            bool lapLimited = 0 < maxLaps && maxLaps < RaceEnd.LapLimitMax;
            if (!(timed || lapLimited))
                return null;
            if (ChequeredEt.HasValue)     // 체커 뒤 통과면 피니시, 같은 틱에 리더보다 먼저면 마지막 랩
                return tCross >= ChequeredEt.Value ? 0 : 1;
            VehicleInfo leader = RaceEnd.RaceLeader(snap);
            bool leadMe = leader == null || leader.Id == me.Id;
            if (leadMe)
            {
                if ((timed && tCross >= endEt) || (lapLimited && me.TotalLaps >= maxLaps))
                    return 0;
            }
            else if (timed && tCross >= endEt)
            {
                return 1;                 // 시계는 끝났고 리더는 아직 — 다음 통과가 피니시
            }
            double? p = state.BaselineLapTime();
            if (!p.HasValue || p.Value <= 0)
                p = RaceEnd.LapPace(me);
            if (!p.HasValue || p.Value <= 0)
                return lapLimited ? Math.Max(maxLaps - me.TotalLaps, 1) : (int?)null;
            double pace = p.Value;
            if (leadMe)
            {
                int c = int.MaxValue;
                if (timed)
                    c = Math.Min(c, (int)Math.Ceiling((endEt - tCross) / pace));
                if (lapLimited)
                    c = Math.Min(c, maxLaps - me.TotalLaps);
                return Math.Max(c, 1);
            }
            double lpace = RaceEnd.LapPace(leader) ?? pace;
            double nLead = now + (1.0 - RaceEnd.LapFrac(leader, now, trackLen, lpace)) * lpace;
            double fLead = double.MaxValue;
            if (timed)
            {
                fLead = Math.Min(fLead, nLead >= endEt
                    ? nLead
                    : nLead + Math.Ceiling((endEt - nLead) / lpace) * lpace);
            }
            if (lapLimited)
                fLead = Math.Min(fLead, nLead + Math.Max(maxLaps - leader.TotalLaps - 1, 0) * lpace);
            int laps = Math.Max((int)Math.Ceiling((fLead - tCross) / pace), 1);
            if (lapLimited)
                laps = Math.Min(laps, Math.Max(maxLaps - me.TotalLaps, 1));
            return laps;
        }
    }
}
