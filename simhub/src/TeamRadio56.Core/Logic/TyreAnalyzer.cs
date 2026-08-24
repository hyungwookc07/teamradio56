using System;
using System.Collections.Generic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// analyzers/tyres.py 포팅 — 랩 히스토리 추세 기반 타이어 분석.
    /// 좌/우·전/후 온도 불균형, 마모율 → 예상 수명, 펑크 즉시 콜.
    /// </summary>
    public sealed class TyreAnalyzer
    {
        private static readonly string[] WheelNames =
        {
            "front left", "front right", "rear left", "rear right",
        };
        private const double WearCliff = 0.25;
        private const int TrendWindow = 3;

        private readonly double _tempImbalance;
        private readonly double _wearWarn;

        public TyreAnalyzer(double tempImbalance = 12.0, double wearWarn = 0.35)
        {
            _tempImbalance = tempImbalance;
            _wearWarn = wearWarn;
        }

        /// <summary>가공된 타이어 상태 요약. 데이터 부족이면 null.</summary>
        public Dictionary<string, object> Status(RaceState state)
        {
            var valid = new List<LapRecord>();
            foreach (LapRecord l in state.Laps)
            {
                if (l.Valid && l.TyreWear != null && l.TyreWear.Length == 4)
                    valid.Add(l);
            }
            if (valid.Count == 0)
                return null;
            LapRecord last = valid[valid.Count - 1];

            var result = new Dictionary<string, object>
            {
                { "wear", last.TyreWear },        // 잔량 비율 FL FR RL RR
                { "temps", last.TyreTemps },      // 캐리커스 C
            };

            // 온도 불균형 (좌우 / 전후)
            double[] t = last.TyreTemps;
            if (t != null && t.Length == 4
                && t[0] > 0 && t[1] > 0 && t[2] > 0 && t[3] > 0)
            {
                result["imbalance"] = new Dictionary<string, object>
                {
                    { "front_lr", Math.Round(t[1] - t[0], 1) },
                    { "rear_lr", Math.Round(t[3] - t[2], 1) },
                    { "front_rear", Math.Round((t[0] + t[1]) / 2 - (t[2] + t[3]) / 2, 1) },
                };
            }

            // 마모 추세 → 예상 수명 (최악 휠 기준)
            if (valid.Count > TrendWindow)
            {
                LapRecord baseLap = valid[valid.Count - 1 - TrendWindow];
                Dictionary<string, object> worst = null;
                double worstLeft = 0;
                for (int i = 0; i < 4; i++)
                {
                    double rate = (baseLap.TyreWear[i] - last.TyreWear[i]) / TrendWindow;
                    if (rate <= 1e-4)
                        continue;
                    double lapsLeft = Math.Max((last.TyreWear[i] - WearCliff) / rate, 0.0);
                    if (worst == null || lapsLeft < worstLeft)
                    {
                        worstLeft = lapsLeft;
                        worst = new Dictionary<string, object>
                        {
                            { "wheel", WheelNames[i] },
                            { "wheel_idx", i },
                            { "wear_rate", Math.Round(rate, 4) },
                            { "remaining", Math.Round(last.TyreWear[i], 3) },
                            { "laps_left", Math.Round(lapsLeft, 1) },
                        };
                    }
                }
                if (worst != null)
                    result["worst"] = worst;
            }
            return result;
        }

        public Dictionary<string, object> OnLap(RaceState state, Snapshot snap,
                                                EventBus bus)
        {
            Dictionary<string, object> st = Status(state);
            if (st == null)
                return null;

            // 펑크/탈락은 즉시 긴급 콜
            WheelInfo[] wheels = snap.Player != null && snap.Player.Wheels != null
                ? snap.Player.Wheels : new WheelInfo[0];
            for (int i = 0; i < Math.Min(wheels.Length, 4); i++)
            {
                if (wheels[i].Flat || wheels[i].Detached)
                {
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.Damage,
                        Priority = Priority.Critical,
                        DedupKey = "flat_" + i,
                        Message = "Puncture, " + WheelNames[i] + "! Box now, take it easy.",
                    };
                    ev.Data["wheel"] = WheelNames[i];
                    bus.Push(ev);
                }
            }

            // 온도 불균형 경고
            object imbObj;
            if (st.TryGetValue("imbalance", out imbObj))
            {
                var imb = (Dictionary<string, object>)imbObj;
                double frontLr = (double)imb["front_lr"];
                double rearLr = (double)imb["rear_lr"];
                // 파이썬 max(): 앞이 크거나 같으면 앞 유지
                string axis = Math.Abs(rearLr) > Math.Abs(frontLr) ? "rear_lr" : "front_lr";
                double delta = axis == "front_lr" ? frontLr : rearLr;
                if (Math.Abs(delta) >= _tempImbalance)
                {
                    string hot = axis == "front_lr"
                        ? (delta > 0 ? WheelNames[1] : WheelNames[0])
                        : (delta > 0 ? WheelNames[3] : WheelNames[2]);
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.TyreWarning,
                        Priority = Priority.Normal,
                    };
                    ev.Data["kind"] = "temp_imbalance";
                    ev.Data["hot_wheel"] = hot;
                    ev.Data["delta"] = Math.Round(Math.Abs(delta), 1);
                    foreach (KeyValuePair<string, object> kv in st)
                        ev.Data[kv.Key] = kv.Value;
                    bus.Push(ev);
                }
            }

            // 예상 수명 경고
            object worstObj;
            if (st.TryGetValue("worst", out worstObj))
            {
                var worst = (Dictionary<string, object>)worstObj;
                if ((double)worst["remaining"] <= _wearWarn)
                {
                    var ev = new RadioEvent
                    {
                        Type = EventTypes.TyreWarning,
                        Priority = Priority.High,
                    };
                    ev.Data["kind"] = "wear";
                    foreach (KeyValuePair<string, object> kv in worst)
                        ev.Data[kv.Key] = kv.Value;
                    foreach (KeyValuePair<string, object> kv in st)
                        ev.Data[kv.Key] = kv.Value;
                    bus.Push(ev);
                }
            }
            return st;
        }
    }
}
