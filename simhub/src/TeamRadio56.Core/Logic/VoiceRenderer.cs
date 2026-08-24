using System;
using System.Collections.Generic;
using System.Globalization;

namespace TeamRadio56.Core.Logic
{
    /// <summary>
    /// voice.py VoiceGenerator의 비-LLM 경로 포팅 — 이벤트 → 멘트 텍스트.
    ///
    /// 긴급 콜은 변형 풀에서 즉시, 비긴급은 템플릿 폴백을 쓴다.
    /// (LLM 멘트/브리지는 아직 파이썬 엔진 담당 — 이식 마지막 단계.)
    /// </summary>
    public sealed class VoiceRenderer
    {
        private readonly PhrasePool _pool;

        public VoiceRenderer(PhrasePool pool = null)
        {
            _pool = pool ?? new PhrasePool();
        }

        public PhrasePool Pool
        {
            get { return _pool; }
        }

        /// <summary>(멘트 텍스트, 소스). 텍스트 null이면 침묵.</summary>
        public KeyValuePair<string, string> TextFor(RadioEvent ev)
        {
            if (!string.IsNullOrEmpty(ev.Message))
            {
                string src = ev.Type == EventTypes.BridgeFollowup ? "llm" : "composed";
                return new KeyValuePair<string, string>(ev.Message, src);
            }
            string text = Render(ev);
            return new KeyValuePair<string, string>(text, text != null ? "cache" : "none");
        }

        private string Render(RadioEvent ev)
        {
            Dictionary<string, object> d = ev.Data;
            string tone = ev.Tone ?? "casual";
            switch (ev.Type)
            {
                case EventTypes.TrafficApproach:
                {
                    int g = Clamp(GetInt(d, "gap_sec", 4), 1, 6);
                    return _pool.Pick("traffic_approach", new Dictionary<string, string>
                    {
                        { "cls", TrafficAnalyzer.ClassName(GetStr(d, "cls")) },
                        { "gap", g + " second" + (g > 1 ? "s" : "") },
                    }, tone);
                }
                case EventTypes.TrafficClose:
                case EventTypes.TrafficUpdate:
                {
                    string pool = GetStr(d, "pool");
                    if (pool.Length == 0)
                        return null;
                    return _pool.Pick(pool, new Dictionary<string, string>
                    {
                        { "cls", TrafficAnalyzer.ClassName(GetStr(d, "cls")) },
                    }, tone);
                }
                case EventTypes.FuelCritical:
                case EventTypes.FuelWarning:
                {
                    int laps = Clamp(GetInt(d, "fuel_laps", 2), 1, 4);
                    return _pool.Pick(ev.Type, new Dictionary<string, string>
                    {
                        { "fuel_laps", laps.ToString(CultureInfo.InvariantCulture) },
                    }, tone);
                }
                case EventTypes.PitCall:
                case EventTypes.Damage:
                case EventTypes.Penalty:
                    return _pool.Pick(ev.Type, new Dictionary<string, string>(), tone);
                case EventTypes.RaceStart:
                case EventTypes.Fcy:
                case EventTypes.FcyPitOpen:
                case EventTypes.GreenFlag:
                case EventTypes.PitLimiter:
                case EventTypes.BlueFlag:
                {
                    string pool = GetStr(d, "pool");
                    if (pool.Length == 0)
                        return null;
                    return _pool.Pick(pool, new Dictionary<string, string>(), tone);
                }
                case EventTypes.Spotter:
                {
                    string pool = GetStr(d, "pool");
                    if (pool.Length == 0)
                        return null;
                    return _pool.Pick(pool, new Dictionary<string, string>
                    {
                        { "side", GetStr(d, "side") },
                    }, tone);
                }
                case EventTypes.RaceEnd:
                {
                    int place = Clamp(GetInt(d, "class_place", 1), 1, 8);
                    return _pool.Pick("race_end", new Dictionary<string, string>
                    {
                        { "place", place.ToString(CultureInfo.InvariantCulture) },
                    }, tone);
                }
                case EventTypes.PositionChange:
                {
                    string pool = GetStr(d, "pool");
                    if (pool.Length == 0)
                        return null;
                    int place = Clamp(GetInt(d, "class_place", 1), 1, 8);
                    return _pool.Pick(pool, new Dictionary<string, string>
                    {
                        { "place", place.ToString(CultureInfo.InvariantCulture) },
                    }, tone);
                }
                case EventTypes.RaceMilestone:
                {
                    if (!d.ContainsKey("remaining_min"))
                        return null;
                    int m = GetInt(d, "remaining_min", 0);
                    if (m >= 60)
                    {
                        int h = m / 60;
                        return h + " hour" + (h > 1 ? "s" : "") + " to go. On plan.";
                    }
                    return m + " minutes to go. Rechecking fuel and tyres.";
                }
                case EventTypes.LapAnalysis:
                {
                    if (d.ContainsKey("pit_window_laps"))
                    {
                        return "Pit window open. Box within "
                               + GetInt(d, "pit_window_laps", 0) + " laps.";
                    }
                    return null;
                }
                case EventTypes.StintBriefing:
                    return "New stint. Easy on the tyres first lap, find the rhythm.";
                case EventTypes.RivalPit:
                {
                    string rel = GetStr(d, "rel") == "앞" ? "ahead" : "behind";
                    string baseText = "P" + GetInt(d, "their_class_place", 0)
                        + " in class, car " + rel + ", just pitted.";
                    if (GetBool(d, "undercut_risk"))
                        return baseText + " Undercut attempt. I'll look at our timing.";
                    return baseText + " I'll call the gap when he's out.";
                }
                case EventTypes.RivalPace:
                {
                    double diff = GetDouble(d, "diff", 0);
                    int laps = GetInt(d, "laps", 0);
                    if (GetStr(d, "mode") == "catch")
                    {
                        return "Car ahead in class is " + F1(diff)
                               + " a lap slower. We catch him in " + laps + ".";
                    }
                    return "Car behind in class is " + F1(diff)
                           + " a lap quicker. With us in " + laps + " laps. Be ready.";
                }
                case EventTypes.TyreWarning:
                {
                    string kind = GetStr(d, "kind");
                    if (kind == "temp_imbalance")
                    {
                        return GetStr(d, "hot_wheel") + " tyre running "
                               + F0(GetDouble(d, "delta", 0)) + " degrees hot. Ease that side.";
                    }
                    if (kind == "wear")
                    {
                        return GetStr(d, "wheel") + " tyre, about "
                               + F0(GetDouble(d, "laps_left", 0))
                               + " laps left. Factoring it in.";
                    }
                    return null;
                }
                case EventTypes.PaceComment:
                {
                    double delta = Math.Abs(GetDouble(d, "delta", 0));
                    if (GetStr(d, "direction") == "slower")
                        return "Lost " + F1(delta) + " on that lap. Checking why.";
                    return F1(delta) + " quicker. Keep the rhythm.";
                }
                case EventTypes.GapComment:
                {
                    double rate = GetDouble(d, "rate", 0);
                    double gap = GetDouble(d, "gap", 0);
                    string gapS = gap < 10 ? F1(gap) : F0(gap);
                    if (GetStr(d, "who") == "behind")
                    {
                        if (rate <= -0.15)
                        {
                            return "Car behind closing " + F1(Math.Abs(rate))
                                   + " a lap. Gap " + gapS + ". Just no mistakes.";
                        }
                        if (rate >= 0.15)
                            return "Gap behind " + gapS + " and opening. Good.";
                        return "Gap behind " + gapS + ", holding. Rhythm's good.";
                    }
                    if (rate <= -0.15)
                    {
                        return "Gap ahead " + gapS + ", closing " + F1(Math.Abs(rate))
                               + " a lap. We can get him.";
                    }
                    if (rate >= 0.15)
                        return "Gap ahead " + gapS + " and opening. Don't overdo it.";
                    return "Gap ahead " + gapS + ", holding. Keep the rhythm.";
                }
                default:
                    return null;    // 렌더러 없는 이벤트 (LLM 전용 등) — 침묵
            }
        }

        // -- 데이터 헬퍼 ------------------------------------------------------

        private static string F1(double v)
        {
            return v.ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string F0(double v)
        {
            return v.ToString("F0", CultureInfo.InvariantCulture);
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null
                ? v.ToString() : "";
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
                return def;
            try
            {
                return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return def;
            }
        }

        private static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
                return def;
            try
            {
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return def;
            }
        }

        private static bool GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null)
                return false;
            if (v is bool b)
                return b;
            try
            {
                return Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// voice.py iter_pregen_texts 포팅 — 사전 캐시 대상 (톤, 텍스트) 전체 열거.
    /// 런타임 렌더러와 동일한 슬롯 조합이어야 캐시가 히트한다.
    /// 파이썬과의 집합 비교로 멘트 데이터/슬롯 포매팅 포팅을 검증한다.
    /// </summary>
    public static class PregenTexts
    {
        public static IEnumerable<KeyValuePair<string, string>> Enumerate(PhrasePool pool)
        {
            string[] classes = { "Hypercar", "LMP2", "GT3", "GTE", "faster car", "" };

            var slotValues = new List<KeyValuePair<string, List<Dictionary<string, string>>>>();

            var approach = new List<Dictionary<string, string>>();
            foreach (string c in classes)
            {
                if (c.Length == 0)
                    continue;
                for (int g = 1; g <= 6; g++)
                {
                    approach.Add(new Dictionary<string, string>
                    {
                        { "cls", c },
                        { "gap", g + " second" + (g > 1 ? "s" : "") },
                    });
                }
            }
            Add(slotValues, "traffic_approach", approach);
            Add(slotValues, "alongside", Empty());
            Add(slotValues, "alongside_left", Empty());
            Add(slotValues, "alongside_right", Empty());
            Add(slotValues, "nearby_behind", PerClass(classes));
            Add(slotValues, "pass_complete", PerClass(classes));
            Add(slotValues, "dropped", PerClass(classes));
            Add(slotValues, "backmarker_ahead", PerClass(classes));
            Add(slotValues, "blue_flag", Empty());
            Add(slotValues, "alongside_both", Empty());
            Add(slotValues, "side_clear", Sides());
            Add(slotValues, "fuel_warning", Range("fuel_laps", 1, 4));
            Add(slotValues, "fuel_critical", Range("fuel_laps", 1, 4));
            Add(slotValues, "pit_call", Empty());
            Add(slotValues, "damage", Empty());
            Add(slotValues, "penalty", Empty());
            Add(slotValues, "race_start", Empty());
            Add(slotValues, "fcy_start", Empty());
            Add(slotValues, "fcy_pit_open", Empty());
            Add(slotValues, "green_flag", Empty());
            Add(slotValues, "pit_limiter", Empty());
            Add(slotValues, "race_end", Range("place", 1, 8));
            Add(slotValues, "position_up", Range("place", 1, 8));
            Add(slotValues, "position_down", Range("place", 1, 8));

            foreach (KeyValuePair<string, List<Dictionary<string, string>>> entry in slotValues)
            {
                foreach (string tone in new[] { "casual", "urgent" })
                {
                    foreach (string phrase in pool.Lines(entry.Key, tone))
                    {
                        foreach (Dictionary<string, string> slots in entry.Value)
                        {
                            string text = PhrasePool.Format(phrase, slots);
                            if (text != null)
                            {
                                yield return
                                    new KeyValuePair<string, string>(tone, text);
                            }
                        }
                    }
                }
            }
        }

        private static void Add(
            List<KeyValuePair<string, List<Dictionary<string, string>>>> list,
            string key, List<Dictionary<string, string>> combos)
        {
            list.Add(new KeyValuePair<string, List<Dictionary<string, string>>>(key, combos));
        }

        private static List<Dictionary<string, string>> Empty()
        {
            return new List<Dictionary<string, string>>
            {
                new Dictionary<string, string>(),
            };
        }

        private static List<Dictionary<string, string>> PerClass(string[] classes)
        {
            var list = new List<Dictionary<string, string>>();
            foreach (string c in classes)
                list.Add(new Dictionary<string, string> { { "cls", c } });
            return list;
        }

        private static List<Dictionary<string, string>> Sides()
        {
            return new List<Dictionary<string, string>>
            {
                new Dictionary<string, string> { { "side", "left" } },
                new Dictionary<string, string> { { "side", "right" } },
            };
        }

        private static List<Dictionary<string, string>> Range(string key, int lo, int hi)
        {
            var list = new List<Dictionary<string, string>>();
            for (int n = lo; n <= hi; n++)
            {
                list.Add(new Dictionary<string, string>
                {
                    { key, n.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                });
            }
            return list;
        }
    }
}
