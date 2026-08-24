using System;
using System.Collections.Generic;
using System.Globalization;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/strategy.py 포팅 — "판단이 필요한 순간"에만 LLM 전략 멘트
    /// 트리거를 만든다 (피트 윈도우 개방, 갭 추세 반전, 날씨/웻니스 변화).
    /// 트리거가 없으면 침묵.
    /// </summary>
    public sealed class StrategyEngine
    {
        private const double GapRateMin = 0.3;
        private const double RainDeltaMin = 0.15;

        private readonly double _wetThreshold;
        private bool _pitNeeded;
        private double? _lastRain;
        private bool _wet;

        public StrategyEngine(double wetnessCrossover = 0.20)
        {
            _wetThreshold = wetnessCrossover;
        }

        public void Reset()
        {
            _pitNeeded = false;
            _lastRain = null;
            _wet = false;
        }

        /// <summary>파이썬 f-string과 같은 float 표기.</summary>
        private static string PyFloat(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture) + ".0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public void OnLap(RaceState state, Snapshot snap, EventBus bus,
                          Dictionary<string, object> fuelStatus,
                          Dictionary<string, object> tyreStatus)
        {
            var triggers = new List<string>();

            // 1) 피트 윈도우 개방 전이
            if (fuelStatus != null)
            {
                object pn;
                bool pitNeeded = fuelStatus.TryGetValue("pit_needed", out pn)
                                 && pn is bool && (bool)pn;
                if (pitNeeded && !_pitNeeded)
                    triggers.Add("피트 윈도우가 방금 열렸다. 타이어 상태와 엮어 언제 들어올지 판단해라.");
                _pitNeeded = pitNeeded;
                object pw;
                if (pitNeeded && fuelStatus.TryGetValue("pit_window_laps", out pw)
                    && pw != null)
                {
                    state.SetIssue("fuel", "연료 " + PyFloat((double)fuelStatus["fuel_laps"])
                        + "랩 분량, 늦어도 " + pw + "랩 안 피트 필요");
                }
                else
                {
                    state.ClearIssue("fuel");
                }
            }

            // 2) 갭 추세 반전 (같은 클래스 앞차)
            string rev = GapReversal(state);
            if (rev != null)
                triggers.Add(rev);

            // 3) 날씨 변화
            double rain = snap.Session.Raining;
            if (!_lastRain.HasValue)
            {
                _lastRain = rain;
            }
            else if (Math.Abs(rain - _lastRain.Value) >= RainDeltaMin)
            {
                string direction = rain > _lastRain.Value ? "강해지고" : "약해지고";
                triggers.Add("비가 " + direction + " 있다 (강수 "
                    + _lastRain.Value.ToString("F1", CultureInfo.InvariantCulture) + "→"
                    + rain.ToString("F1", CultureInfo.InvariantCulture) + "). "
                    + "타이어/피트 전략 판단을 말해라.");
                state.SetIssue("weather", "강수 변화 중 ("
                    + rain.ToString("F1", CultureInfo.InvariantCulture) + ")");
                _lastRain = rain;
            }
            else if (rain < 0.05)
            {
                state.ClearIssue("weather");
            }

            // 3-b) 노면 웻니스 크로스오버 (슬릭 ↔ 웻 판단 지점)
            double wetness = snap.Session.AvgWetness;
            if (!_wet && wetness >= _wetThreshold)
            {
                _wet = true;
                triggers.Add("노면이 젖어 슬릭 한계에 도달했다 (웻니스 "
                    + wetness.ToString("F2", CultureInfo.InvariantCulture) + "). "
                    + "웻 타이어 전환 판단을 말해라.");
                state.SetIssue("track_wet", "노면 젖음 — 슬릭 한계 구간");
            }
            else if (_wet && wetness <= _wetThreshold * 0.5)
            {
                _wet = false;
                triggers.Add("노면이 마르면서 드라이 라인이 나오고 있다. "
                             + "슬릭 복귀 타이밍 판단을 말해라.");
                state.ClearIssue("track_wet");
            }

            // 타이어 이슈 유지
            if (tyreStatus != null)
            {
                object worstObj;
                if (tyreStatus.TryGetValue("worst", out worstObj) && worstObj != null)
                {
                    var w = (Dictionary<string, object>)worstObj;
                    double lapsLeft = (double)w["laps_left"];
                    if (lapsLeft <= 15)
                    {
                        state.SetIssue("tyres", w["wheel"] + " 타이어 수명 약 "
                            + lapsLeft.ToString("F0", CultureInfo.InvariantCulture) + "랩");
                    }
                    else
                    {
                        state.ClearIssue("tyres");
                    }
                }
            }

            if (triggers.Count == 0)
                return;
            int lapNo = state.Laps.Count > 0
                ? state.Laps[state.Laps.Count - 1].LapNumber : 0;
            var ev = new RadioEvent
            {
                Type = EventTypes.LapAnalysis,
                Priority = Priority.Normal,
                DedupKey = "strategy_" + lapNo,
            };
            if (fuelStatus != null)
            {
                foreach (KeyValuePair<string, object> kv in fuelStatus)
                    ev.Data[kv.Key] = kv.Value;
            }
            ev.Data["triggers"] = triggers.ToArray();
            bus.Push(ev);
        }

        /// <summary>최근 갭 변화율의 부호가 이전 구간과 뒤집혔는가.</summary>
        private static string GapReversal(RaceState state)
        {
            var valid = new List<LapRecord>();
            foreach (LapRecord l in state.Laps)
            {
                if (l.Valid && l.GapAhead >= 0)
                    valid.Add(l);
            }
            if (valid.Count < 5)
                return null;
            double recent = (valid[valid.Count - 1].GapAhead
                             - valid[valid.Count - 3].GapAhead) / 2;
            double prev = (valid[valid.Count - 3].GapAhead
                           - valid[valid.Count - 5].GapAhead) / 2;
            if (Math.Abs(recent) < GapRateMin || Math.Abs(prev) < GapRateMin)
                return null;
            if ((recent > 0) == (prev > 0))
                return null;
            string direction = recent > 0 ? "좁혀지다가 다시 벌어지기" : "벌어지다가 다시 좁혀지기";
            return "앞차와의 갭이 " + direction + " 시작했다. 페이스 전략 판단을 말해라.";
        }
    }
}
