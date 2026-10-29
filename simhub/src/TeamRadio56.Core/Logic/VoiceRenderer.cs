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

        /// <summary>
        /// 변형 풀(랜덤)을 안 쓰는 렌더러 — voice.py DETERMINISTIC_TYPES와 같은 집합.
        /// </summary>
        public static readonly string[] DeterministicTypes =
        {
            EventTypes.GapComment, EventTypes.PaceComment, EventTypes.LapAnalysis,
            EventTypes.StintBriefing, EventTypes.RivalPit, EventTypes.RivalPace,
            EventTypes.TyreWarning, EventTypes.RaceMilestone,
        };

        /// <summary>
        /// voice.render_deterministic 대응 — 리플레이 'say' 대조용. 조립 멘트
        /// (Message 있음)나 대상 밖 타입은 null.
        /// </summary>
        public string RenderDeterministic(RadioEvent ev)
        {
            if (!string.IsNullOrEmpty(ev.Message)
                || Array.IndexOf(DeterministicTypes, ev.Type) < 0)
                return null;
            return Render(ev);
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
                        { "cls", Messages.ClassDisplay(GetStr(d, "cls")) },
                        { "gap", Messages.GapSlot(g) },
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
                        { "cls", Messages.ClassDisplay(GetStr(d, "cls")) },
                    }, tone);
                }
                case EventTypes.FuelCritical:
                case EventTypes.FuelWarning:
                {
                    // 파이썬 int()는 절사 — 반올림하면 슬롯이 어긋난다.
                    // 슬롯 값이 단위를 품는다 ("1 lap"/"3 laps") — 복수형 실수 방지
                    int laps = (int)Math.Min(Math.Max(GetDouble(d, "fuel_laps", 2), 1), 4);
                    return _pool.Pick(ev.Type, new Dictionary<string, string>
                    {
                        { "fuel_laps", Messages.FuelSlot(laps) },
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
                        { "side", Messages.SideSlot(GetStr(d, "side")) },
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
                    object rm;
                    if (d == null || !d.TryGetValue("remaining_min", out rm) || rm == null)
                        return null;
                    int m = GetInt(d, "remaining_min", 0);
                    if (m >= 60)
                    {
                        int h = m / 60;
                        return Messages.Get("milestone_hours", "h", h,
                            "s", (h > 1 && !Messages.IsKo) ? "s" : "");
                    }
                    return Messages.Get("milestone_minutes", "m", m);
                }
                case EventTypes.LapAnalysis:
                {
                    // 연료 상태를 통째로 실어 피트 불필요면 키가 있고 값이 null —
                    // 파이썬 d.get(...) is not None과 같게 null도 침묵
                    object pw;
                    if (d != null && d.TryGetValue("pit_window_laps", out pw) && pw != null)
                    {
                        return Messages.Get("pit_window",
                            "n", Messages.LapsText(GetInt(d, "pit_window_laps", 0)));
                    }
                    return null;
                }
                case EventTypes.StintBriefing:
                    return Messages.Get("stint_brief");
                case EventTypes.RivalPit:
                {
                    string rel = Messages.Get(GetStr(d, "rel") == "앞"
                        ? "rival_rel_ahead" : "rival_rel_behind");
                    string baseText = Messages.Get("rival_pit_base",
                        "rel", rel, "p", GetInt(d, "their_class_place", 0));
                    if (GetBool(d, "undercut_risk"))
                        return baseText + Messages.Get("rival_pit_undercut");
                    return baseText + Messages.Get("rival_pit_gap");
                }
                case EventTypes.RivalPace:
                {
                    double diff = GetDouble(d, "diff", 0);
                    int laps = GetInt(d, "laps", 0);
                    if (GetStr(d, "mode") == "catch")
                    {
                        return Messages.Get("rival_catch",
                            "diff", diff, "laps", Messages.LapsText(laps));
                    }
                    return Messages.Get("rival_defend",
                        "diff", diff, "laps", Messages.LapsText(laps));
                }
                case EventTypes.TyreWarning:
                {
                    string kind = GetStr(d, "kind");
                    if (kind == "temp_imbalance")
                    {
                        return Messages.Get("tyre_hot",
                            "wheel", Messages.WheelDisplay(GetStr(d, "hot_wheel")),
                            "delta", GetDouble(d, "delta", 0));
                    }
                    if (kind == "wear")
                    {
                        return Messages.Get("tyre_wear",
                            "wheel", Messages.WheelDisplay(GetStr(d, "wheel")),
                            "laps", Messages.LapsText(GetDouble(d, "laps_left", 0)));
                    }
                    return null;
                }
                case EventTypes.PaceComment:
                {
                    double delta = Math.Abs(GetDouble(d, "delta", 0));
                    if (GetStr(d, "direction") == "slower")
                        return Messages.Get("pace_lost", "delta", delta);
                    return Messages.Get("pace_quick", "delta", delta);
                }
                case EventTypes.GapComment:
                {
                    double rate = GetDouble(d, "rate", 0);
                    double gap = GetDouble(d, "gap", 0);
                    string gapS = Messages.SecondsText(gap);   // 단위 포함
                    if (GetStr(d, "who") == "behind")
                    {
                        if (rate <= -0.15)
                        {
                            return Messages.Get("gap_behind_closing",
                                "rate", Math.Abs(rate), "gap", gapS);
                        }
                        if (rate >= 0.15)
                            return Messages.Get("gap_behind_opening", "gap", gapS);
                        return Messages.Get("gap_behind_holding", "gap", gapS);
                    }
                    if (rate <= -0.15)
                    {
                        return Messages.Get("gap_ahead_closing",
                            "rate", Math.Abs(rate), "gap", gapS);
                    }
                    if (rate >= 0.15)
                        return Messages.Get("gap_ahead_opening", "gap", gapS);
                    return Messages.Get("gap_ahead_holding", "gap", gapS);
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

        /// <summary>Messages.SecondsText 위임 (기존 호출부 호환).</summary>
        internal static string SecondsText(double sec)
        {
            return Messages.SecondsText(sec);
        }

        /// <summary>Messages.LapsText 위임 (기존 호출부 호환).</summary>
        internal static string LapsText(double n)
        {
            return Messages.LapsText(n);
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
            string[] classes = Messages.IsKo
                ? new[] { "하이퍼카", "엘엠피 투", "GT3", "GTE", "상위 클래스", "" }
                : new[] { "Hypercar", "LMP2", "GT3", "GTE", "faster car", "" };

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
                        { "gap", Messages.GapSlot(g) },
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
            Add(slotValues, "fuel_warning", FuelSlots());
            Add(slotValues, "fuel_critical", FuelSlots());
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
            if (!Messages.IsKo)
            {
                foreach (KeyValuePair<string, string> item in ComposedWhole())
                    yield return item;
            }
        }

        // 페널티 콜 열거 — RaceControlAnalyzer.ParsePenalty가 내는 값 전체
        // (reason ""은 사유 미상). voice.py PEN_KINDS/PEN_REASONS와 일치.
        internal static readonly string[] PenaltyKinds =
            { "drive-through", "stop-and-go", "time penalty", "penalty" };
        internal static readonly string[] PenaltyReasons =
        {
            "", "pit lane speeding", "track limits", "yellow flag infringement",
            "start infringement", "contact", "blocking", "unsafe rejoin",
        };

        /// <summary>
        /// voice.py iter_composed_whole_texts 미러 — 분석기가 조립하는 문장 중
        /// 경우의 수가 작은 것을 통째로 사전 생성 (en 전용, ko는 빈 열거).
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> ComposedWhole()
        {
            if (Messages.IsKo)
                yield break;
            for (int n = 0; n <= 40; n++)
                yield return Casual(Messages.Get("pit_window", "n", Messages.LapsText(n)));
            foreach (string rel in new[] { "ahead", "behind" })
            {
                // RivalAnalyzer: undercut_risk == (뒤차가 먼저 피트)
                string tail = Messages.Get(rel == "behind"
                    ? "rival_pit_undercut" : "rival_pit_gap");
                for (int p = 1; p <= 30; p++)
                {
                    yield return Casual(Messages.Get("rival_pit_base",
                        "rel", Messages.Get("rival_rel_" + rel), "p", p) + tail);
                }
            }
            var renderer = new VoiceRenderer(new PhrasePool(new Random(0)));
            foreach (int m in RaceControlAnalyzer.MilestonesMin)
            {
                yield return Casual(renderer.RenderDeterministic(new RadioEvent
                {
                    Type = EventTypes.RaceMilestone,
                    Data = new Dictionary<string, object> { { "remaining_min", m } },
                }));
            }
            foreach (string key in new[] { "stint_brief", "final_lap", "pen_clear",
                                           "dmg_no_effect", "report_marks",
                                           "report_clean", "align_mild" })
                yield return Casual(Messages.Get(key));
            for (int i = 0; i < 4; i++)
            {
                yield return Casual(Messages.Get("slow_puncture", "wheel", Messages.WheelName(i)));
                yield return Casual(Messages.Get("puncture_now", "wheel", Messages.WheelName(i)));
            }
            foreach (string what in new[] { "engine_what_water", "engine_what_oil",
                                            "engine_what_engine" })
                yield return Casual(Messages.Get("engine_warn", "what", Messages.Get(what)));

            for (int n = 1; n <= 3; n++)
                yield return Urgent(Messages.Get("sector_yellow", "n", n));
            yield return Urgent(Messages.Get("stopped_hazard"));
            for (int i = 0; i < 4; i++)
                yield return Urgent(Messages.Get("wheel_gone", "wheel", Messages.WheelName(i)));
            foreach (string key in new[] { "part_detached_rear", "part_detached",
                                           "align_severe", "rear_instab" })
                yield return Urgent(Messages.Get(key));
            foreach (string kind in PenaltyKinds)
            {
                foreach (string reason in PenaltyReasons)
                    yield return Urgent(Messages.PenaltyText(kind, reason));
            }
        }

        private static KeyValuePair<string, string> Casual(string text)
        {
            return new KeyValuePair<string, string>("casual", text);
        }

        private static KeyValuePair<string, string> Urgent(string text)
        {
            return new KeyValuePair<string, string>("urgent", text);
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
                new Dictionary<string, string> { { "side", Messages.SideSlot("left") } },
                new Dictionary<string, string> { { "side", Messages.SideSlot("right") } },
            };
        }

        private static List<Dictionary<string, string>> FuelSlots()
        {
            var list = new List<Dictionary<string, string>>();
            for (int n = 1; n <= 4; n++)
            {
                list.Add(new Dictionary<string, string>
                {
                    { "fuel_laps", Messages.FuelSlot(n) },
                });
            }
            return list;
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

    /// <summary>
    /// voice.py iter_pregen_sentences 미러 — 문장 단위 사전 생성 대상 (톤, 문장).
    /// 실제 멘트를 슬롯 도메인 전체에 대해 렌더한 뒤 SplitSentences로 쪼갠다
    /// (중복 제거). en 전용. 도메인 상수는 파이썬 SEG_*와 값이 같아야 한다 —
    /// replay-check가 집합을 diff한다.
    /// </summary>
    public static class PregenSentences
    {
        public static readonly double[] SegGaps = Gaps();
        public static readonly double[] SegRates = Tenths(1, 99);
        public static readonly double[] SegPace = Tenths(7, 99);
        public static readonly double[] SegRivalDiff = Tenths(3, 50);
        public const int SegRivalLapsMin = 1, SegRivalLapsMax = 16;
        public const int SegTyreHotMin = 12, SegTyreHotMax = 40;
        public const int SegTyreLapsMin = 0, SegTyreLapsMax = 30;
        public static readonly string[] SegMultiClasses =
            { "Hypercar", "LMP2", "GT3", "GTE", "faster car" };
        public const int SegMultiMaxCars = 3;
        public const int SegDentMaxZones = 3;

        /// <summary>
        /// maxTier 이하 단계의 (톤, 문장). tracks는 2단계(세션 브리핑)용.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, string>> Enumerate(
            int maxTier = 2, IEnumerable<string> tracks = null)
        {
            if (Messages.IsKo)
                yield break;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (maxTier >= 1)
            {
                foreach (KeyValuePair<string, string> m in Tier1Messages())
                {
                    foreach (string s in Messages.SplitSentences(m.Value))
                    {
                        if (seen.Add(m.Key + "\t" + s))
                            yield return new KeyValuePair<string, string>(m.Key, s);
                    }
                }
            }
        }

        /// <summary>트랙 이름 목록 파일 — 한 줄에 하나, 빈 줄과 '#' 주석은 무시.</summary>
        public static List<string> LoadTracks(string path)
        {
            var list = new List<string>();
            foreach (string line in System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8))
            {
                string t = line.Trim();
                if (t.Length > 0 && !t.StartsWith("#", StringComparison.Ordinal))
                    list.Add(t);
            }
            return list;
        }

        /// <summary>1단계 도메인의 전체 멘트 (분할 전).</summary>
        private static IEnumerable<KeyValuePair<string, string>> Tier1Messages()
        {
            // 갭 — VoiceRenderer GapComment
            foreach (double g in SegGaps)
            {
                string gs = Messages.SecondsText(g);
                foreach (string who in new[] { "behind", "ahead" })
                {
                    yield return C(Messages.Get("gap_" + who + "_opening", "gap", gs));
                    yield return C(Messages.Get("gap_" + who + "_holding", "gap", gs));
                    foreach (double r in SegRates)
                        yield return C(Messages.Get("gap_" + who + "_closing", "rate", r, "gap", gs));
                }
            }
            // 페이스 — PaceComment
            foreach (double d in SegPace)
            {
                yield return C(Messages.Get("pace_lost", "delta", d));
                yield return C(Messages.Get("pace_quick", "delta", d));
            }
            // 라이벌 페이스 — RivalPace
            foreach (double d in SegRivalDiff)
            {
                for (int n = SegRivalLapsMin; n <= SegRivalLapsMax; n++)
                {
                    yield return C(Messages.Get("rival_catch", "diff", d, "laps", Messages.LapsText(n)));
                    yield return C(Messages.Get("rival_defend", "diff", d, "laps", Messages.LapsText(n)));
                }
            }
            // 타이어 — TyreWarning
            for (int i = 0; i < 4; i++)
            {
                for (int d = SegTyreHotMin; d <= SegTyreHotMax; d++)
                    yield return C(Messages.Get("tyre_hot", "wheel", Messages.WheelName(i), "delta", d));
                for (int n = SegTyreLapsMin; n <= SegTyreLapsMax; n++)
                {
                    yield return C(Messages.Get("tyre_wear",
                        "wheel", Messages.WheelName(i), "laps", Messages.LapsText(n)));
                }
            }
            // 데미지 리포트 — HealthAnalyzer.DamageReport (문제 하나 = 한 문장)
            var wheels = new List<string>();
            for (int i = 0; i < 4; i++)
                wheels.Add(Messages.WheelName(i));
            var problems = new List<string>();
            for (int k = 1; k <= 4; k++)
            {
                foreach (List<string> sub in Combinations(wheels, k))
                {
                    problems.Add(Messages.Get("item_wheel_damage", "names", Messages.AndList(sub)));
                    problems.Add(Messages.Get("item_puncture", "names", Messages.AndList(sub)));
                }
            }
            foreach (string w in wheels)
                problems.Add(Messages.Get("item_losing_pressure", "name", w));
            var zones = new List<string>();
            foreach (string z in HealthAnalyzer.DentZones)
                zones.Add(Messages.ZoneDisplay(z));
            for (int k = 1; k <= SegDentMaxZones; k++)
            {
                foreach (List<string> sub in Combinations(zones, k))
                    problems.Add(Messages.Get("item_heavy_body", "zones", Messages.AndList(sub)));
            }
            foreach (string p in problems)
            {
                foreach (string adv in new[] { "report_advice_box", "report_advice_keep" })
                {
                    yield return C(Messages.Get("report_problems",
                        "problems", Messages.JoinProblems(new[] { p }),
                        "advice", Messages.Get(adv)));
                }
            }
            // 다중 차량 — TrafficAnalyzer.ComposeMulti (절 하나 = 한 문장)
            foreach (string c in SegMultiClasses)
            {
                foreach (string key in new[] { "multi_alongside_left", "multi_alongside_right",
                                               "multi_alongside" })
                    yield return U(Messages.Get(key, "cls", c) + ".");
            }
            yield return U(Messages.Get("multi_ahead_clear"));
            for (int k = 1; k <= SegMultiMaxCars; k++)
            {
                foreach (List<string> classes in Permutations(SegMultiClasses, k))
                {
                    foreach (int[] counts in Counts(k, SegMultiMaxCars))
                    {
                        var pairs = new List<KeyValuePair<string, int>>();
                        for (int j = 0; j < k; j++)
                            pairs.Add(new KeyValuePair<string, int>(classes[j], counts[j]));
                        string names = Messages.MultiNames(pairs);
                        yield return U(Messages.Get("multi_behind", "names", names) + ".");
                        yield return U(Messages.Get("multi_closing", "names", names) + ".");
                    }
                }
            }
        }

        private static KeyValuePair<string, string> C(string text)
        {
            return new KeyValuePair<string, string>("casual", text);
        }

        private static KeyValuePair<string, string> U(string text)
        {
            return new KeyValuePair<string, string>("urgent", text);
        }

        private static double[] Tenths(int lo, int hi)
        {
            var v = new double[hi - lo + 1];
            for (int i = lo; i <= hi; i++)
                v[i - lo] = i / 10.0;
            return v;
        }

        private static double[] Gaps()
        {
            var v = new List<double>(Tenths(0, 99));
            for (int s = 10; s <= 120; s++)
                v.Add(s);
            return v.ToArray();
        }

        /// <summary>itertools.combinations — 인덱스 순서 유지.</summary>
        private static IEnumerable<List<string>> Combinations(IList<string> items, int k)
        {
            var idx = new int[k];
            for (int i = 0; i < k; i++)
                idx[i] = i;
            while (k <= items.Count)
            {
                var pick = new List<string>(k);
                foreach (int i in idx)
                    pick.Add(items[i]);
                yield return pick;
                int pos = k - 1;
                while (pos >= 0 && idx[pos] == items.Count - k + pos)
                    pos--;
                if (pos < 0)
                    yield break;
                idx[pos]++;
                for (int j = pos + 1; j < k; j++)
                    idx[j] = idx[j - 1] + 1;
            }
        }

        /// <summary>itertools.permutations — 서로 다른 k개의 순서 있는 나열.</summary>
        private static IEnumerable<List<string>> Permutations(IList<string> items, int k)
        {
            if (k == 0)
            {
                yield return new List<string>();
                yield break;
            }
            for (int i = 0; i < items.Count; i++)
            {
                var rest = new List<string>(items);
                rest.RemoveAt(i);
                foreach (List<string> tail in Permutations(rest, k - 1))
                {
                    tail.Insert(0, items[i]);
                    yield return tail;
                }
            }
        }

        /// <summary>k칸 각 1 이상, 합 max 이하인 대수 조합 (product + 합 필터).</summary>
        private static IEnumerable<int[]> Counts(int k, int max)
        {
            var c = new int[k];
            for (int i = 0; i < k; i++)
                c[i] = 1;
            while (true)
            {
                int sum = 0;
                foreach (int x in c)
                    sum += x;
                if (sum <= max)
                    yield return (int[])c.Clone();
                int pos = k - 1;
                while (pos >= 0 && c[pos] == max)
                {
                    c[pos] = 1;
                    pos--;
                }
                if (pos < 0)
                    yield break;
                c[pos]++;
            }
        }
    }
}
