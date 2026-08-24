using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/pace.py 포팅 — 랩 완료 시 페이스/갭 분석.
    /// 평소(중앙값) 대비 유의미한 랩타임 편차, 갭 변화율, 배틀 문맥
    /// 갭 리포트만 이벤트로 낸다. 그 외엔 침묵.
    /// </summary>
    public sealed class PaceAnalyzer
    {
        private const int MinLapsForBaseline = 4;
        private const double BattleReportGapSec = 10.0;
        private const double BattleMinChangeSec = 0.3;

        private readonly double _paceDelta;
        private readonly double _gapRate;
        private readonly Dictionary<string, double> _lastReport =
            new Dictionary<string, double>();

        public PaceAnalyzer(double paceDeltaSec = 0.7, double gapChangeSecPerLap = 0.4)
        {
            _paceDelta = paceDeltaSec;
            _gapRate = gapChangeSecPerLap;
        }

        public void Reset()
        {
            _lastReport.Clear();
        }

        public void OnLap(RaceState state, Snapshot snap, EventBus bus, LapRecord lap)
        {
            if (!lap.Valid)
                return;
            var valid = new List<LapRecord>();
            foreach (LapRecord l in state.Laps)
            {
                if (l.Valid)
                    valid.Add(l);
            }
            if (valid.Count < MinLapsForBaseline)
                return;

            // 방금 랩을 제외한 최근 유효 랩들의 중앙값 = 평소 페이스
            var prior = new List<double>();
            int start = Math.Max(valid.Count - 1 - 5, 0);
            for (int i = start; i < valid.Count - 1; i++)
                prior.Add(valid[i].LapTime);
            prior.Sort();
            double baseline = prior[prior.Count / 2];
            double delta = lap.LapTime - baseline;

            if (Math.Abs(delta) >= _paceDelta)
            {
                var ev = new RadioEvent
                {
                    Type = EventTypes.PaceComment,
                    Priority = Priority.Normal,
                    DedupKey = "pace_" + lap.LapNumber,
                };
                ev.Data["lap"] = lap.LapNumber;
                ev.Data["lap_time"] = lap.LapTime;
                ev.Data["baseline"] = Math.Round(baseline, 3);
                ev.Data["delta"] = Math.Round(delta, 3);
                ev.Data["place"] = lap.Place;
                ev.Data["direction"] = delta > 0 ? "slower" : "faster";
                bus.Push(ev);
            }

            Dictionary<string, object> gapEv = GapTrend(valid) ?? BattleReport(valid);
            if (gapEv != null)
            {
                _lastReport[(string)gapEv["who"]] = (double)gapEv["gap"];
                bus.Push(new RadioEvent
                {
                    Type = EventTypes.GapComment,
                    Priority = Priority.Normal,
                    Data = gapEv,
                    DedupKey = "gap_" + lap.LapNumber,
                });
            }
        }

        /// <summary>최근 3랩 갭 변화율 (초/랩). 큰 쪽 하나만.</summary>
        private Dictionary<string, object> GapTrend(List<LapRecord> valid)
        {
            if (valid.Count < 3)
                return null;
            LapRecord a = valid[valid.Count - 3];
            LapRecord c = valid[valid.Count - 1];

            var candidates = new List<Dictionary<string, object>>();
            foreach (string who in new[] { "ahead", "behind" })
            {
                double g0 = who == "ahead" ? a.GapAhead : a.GapBehind;
                double g1 = who == "ahead"
                    ? valid[valid.Count - 2].GapAhead : valid[valid.Count - 2].GapBehind;
                double g2 = who == "ahead" ? c.GapAhead : c.GapBehind;
                if (g0 < 0 || g1 < 0 || g2 < 0)     // 갭 정보 없음
                    continue;
                double rate = (g2 - g0) / 2.0;
                if (Math.Abs(rate) >= _gapRate)
                {
                    candidates.Add(new Dictionary<string, object>
                    {
                        { "who", who },
                        { "gap", Math.Round(g2, 1) },
                        { "rate", Math.Round(rate, 2) },
                    });
                }
            }
            if (candidates.Count == 0)
                return null;
            Dictionary<string, object> best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                if (Math.Abs((double)candidates[i]["rate"]) > Math.Abs((double)best["rate"]))
                    best = candidates[i];
            }
            return best;
        }

        /// <summary>배틀 문맥 갭 리포트 — 10초 이내 앞/뒤차, 변화 있을 때만.</summary>
        private Dictionary<string, object> BattleReport(List<LapRecord> valid)
        {
            if (valid.Count < 2)
                return null;
            LapRecord prev = valid[valid.Count - 2];
            LapRecord cur = valid[valid.Count - 1];

            var candidates = new List<Dictionary<string, object>>();
            foreach (string who in new[] { "ahead", "behind" })
            {
                double g = who == "ahead" ? cur.GapAhead : cur.GapBehind;
                double p = who == "ahead" ? prev.GapAhead : prev.GapBehind;
                if (!(0 <= g && g <= BattleReportGapSec) || p < 0)
                    continue;
                double last;
                if (_lastReport.TryGetValue(who, out last)
                    && Math.Abs(g - last) < BattleMinChangeSec)
                {
                    continue;    // 지난 리포트와 비슷하면 침묵
                }
                candidates.Add(new Dictionary<string, object>
                {
                    { "who", who },
                    { "gap", Math.Round(g, 1) },
                    { "rate", Math.Round(g - p, 2) },
                });
            }
            if (candidates.Count == 0)
            {
                // 배틀에서 벗어난 쪽은 기억을 지워 다음 배틀 진입 때 다시 리포트
                foreach (string who in new[] { "ahead", "behind" })
                {
                    double g = who == "ahead" ? cur.GapAhead : cur.GapBehind;
                    if (!(0 <= g && g <= BattleReportGapSec))
                        _lastReport.Remove(who);
                }
                return null;
            }
            Dictionary<string, object> best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                if ((double)candidates[i]["gap"] < (double)best["gap"])
                    best = candidates[i];
            }
            return best;
        }
    }
}
