using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// tools/coverage_fuzz.py eval의 C# 미러 — 같은 케이스(JSONL)를 Core의 순수 조립 함수에
    /// 넣고, 가상 음성 팩(기대 파일명 집합) 위의 VoiceRoute(kokoro).Plan으로 판정해
    /// 파이썬과 바이트 단위로 같은 TSV를 쓴다 (파이썬 전용 kind는 건너뛴다).
    /// </summary>
    public static class Fuzz
    {
        private static readonly string[] Wheels =
            { "front left", "front right", "rear left", "rear right" };

        private static readonly HashSet<string> PyOnly =
            new HashSet<string> { "lap_feedback", "track_trend", "llm_gate" };

        public static int Eval(string casesPath, string outPath, List<string> tracks)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool en = !Messages.IsKo;
            var pool = new PhrasePool(new Random(0));
            VoiceRoute route = en ? VirtualPack(tracks) : null;
            var memo = new Dictionary<string, string>(StringComparer.Ordinal);
            List<KeyValuePair<string, string>> fixedTexts =
                new List<KeyValuePair<string, string>>(PregenTexts.ComposedWhole());

            int n = 0;
            using (var reader = new StreamReader(casesPath, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(outPath, false, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    using JsonDocument doc = JsonDocument.Parse(line);
                    JsonElement c = doc.RootElement;
                    string kind = c.GetProperty("kind").GetString();
                    if (PyOnly.Contains(kind))
                        continue;
                    int id = c.GetProperty("id").GetInt32();
                    string band = c.GetProperty("band").GetString();
                    JsonElement a = c.GetProperty("args");

                    string[] r = EvalCase(kind, a, pool, fixedTexts);
                    string tone = "", text = "", outcome;
                    if (r[0] == "voice" || r[0] == "drop")
                    {
                        tone = r[1];
                        text = r[2];
                        if (!en)
                            outcome = "-";
                        else if (r[0] == "drop")
                            outcome = "DROP:" + r[3];
                        else
                            outcome = Classify(route, memo, tone, text);
                    }
                    else if (r[0] == "suppress")
                    {
                        outcome = "SUPPRESS:" + r[1];
                    }
                    else
                    {
                        outcome = "NONE";
                    }
                    writer.WriteLine(id.ToString(CultureInfo.InvariantCulture) + "\t" + kind + "\t"
                                     + band + "\t" + tone + "\t" + outcome + "\t" + Esc(text));
                    n++;
                }
            }
            Console.WriteLine($"평가 {n}건 ({Messages.Lang}, {sw.Elapsed.TotalSeconds:F1}초) → {outPath}");
            return 0;
        }

        /// <summary>배포 팩이 담는 파일명 전체 — 통문장 _rfx + 문장 세그먼트 + 조립 자산.</summary>
        private static VoiceRoute VirtualPack(List<string> tracks)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var probe = new VoiceCache("pack", "en-GB-RyanNeural", 10, "bm_george", true, "kokoro");
            foreach (KeyValuePair<string, string> item in PregenTexts.Enumerate(new PhrasePool()))
            {
                foreach (string name in probe.CandidateFileNames(item.Value, item.Key))
                {
                    names.Add(Path.GetFileNameWithoutExtension(name) + "_rfx"
                              + VoiceCache.RadioFxVersion + ".wav");
                    break;   // 첫 후보가 kokoro
                }
            }
            foreach (KeyValuePair<string, string> item
                     in PregenSentences.Enumerate(PregenSentences.PackMaxTier, tracks))
                names.Add(VoiceCache.SegmentFileName(item.Value, item.Key));
            foreach (string asset in RadioAssembler.AssetNames)
                names.Add(asset);
            var cache = new VoiceCache("pack", "en-GB-RyanNeural", 10, "bm_george", true, "kokoro",
                                       p => names.Contains(Path.GetFileName(p)));
            return new VoiceRoute(cache, VoiceKind.Kokoro, false);
        }

        private static string Classify(VoiceRoute route, Dictionary<string, string> memo,
                                       string tone, string text)
        {
            string key = tone + "\t" + text;
            string outcome;
            if (memo.TryGetValue(key, out outcome))
                return outcome;
            string firstMissing;
            VoicePlan plan = route.Plan(text, tone, out firstMissing);
            outcome = plan.Source == VoiceSource.KokoroFx ? "WHOLE"
                : plan.Source == VoiceSource.KokoroSeg ? "SEG"
                : "MISS:" + (firstMissing ?? "None");
            memo[key] = outcome;
            return outcome;
        }

        private static string[] Voice(string tone, string text)
        {
            return text == null ? new[] { "none" } : new[] { "voice", tone, text };
        }

        private static string[] EvalCase(string kind, JsonElement a, PhrasePool pool,
                                         List<KeyValuePair<string, string>> fixedTexts)
        {
            switch (kind)
            {
                case "pool":
                {
                    var ev = new RadioEvent { Type = Str(a, "ev"), Data = Dict(a.GetProperty("data")) };
                    string key;
                    Dictionary<string, string> slots;
                    if (!VoiceRenderer.PoolCall(ev, out key, out slots))
                        return new[] { "none" };
                    string tone = Str(a, "tone");
                    string[] lines = pool.Lines(key, tone);
                    int line = Int(a, "line");
                    if (line >= lines.Length)
                        return new[] { "none" };
                    return Voice(tone, PhrasePool.Format(lines[line], slots));
                }
                // 억제 판단은 분석기의 순수 함수가 한다 (하네스는 임계 미만 = NONE만 가른다) —
                // 한쪽 엔진에서만 규칙이 빠지면 파이썬 ↔ C# TSV 불일치로 드러난다
                case "gap":
                {
                    Dictionary<string, object> d =
                        PaceAnalyzer.GapCandidate(Str(a, "who"), Dbl(a, "gap"), Dbl(a, "rate"));
                    if (d == null)
                        return new[] { "suppress", "gap" };
                    string text = Det(EventTypes.GapComment, d);
                    if (!SpeechDomain.GapInPack((double)d["gap"]))
                        return new[] { "drop", "casual", text, "gap-range" };
                    return Voice("casual", text);
                }
                case "pace":
                {
                    double d = Dbl(a, "delta");
                    if (Math.Abs(d) < SpeechDomain.PaceMin)
                        return new[] { "none" };
                    string direction = PaceAnalyzer.CommentDirection(d, SpeechDomain.PaceMin);
                    if (direction == null)
                        return new[] { "suppress", "pace" };
                    var data = new Dictionary<string, object>
                    {
                        { "delta", d }, { "direction", direction },
                    };
                    return Voice("casual", Det(EventTypes.PaceComment, data));
                }
                case "rival_pace":
                {
                    double diff = Dbl(a, "diff");
                    if (diff < SpeechDomain.RivalDiffMin)
                        return new[] { "none" };
                    bool catchMode = Str(a, "mode") == "catch";
                    Dictionary<string, object> data =
                        RivalAnalyzer.PaceData(catchMode ? diff : -diff, Dbl(a, "gap"), catchMode);
                    if (data == null)
                        return new[] { "suppress", "rival-diff" };
                    return Voice("casual", Det(EventTypes.RivalPace, data));
                }
                case "rival_pit":
                {
                    Dictionary<string, object> data = RivalAnalyzer.PitData(Int(a, "their"), Int(a, "my"));
                    if (data == null)
                        return new[] { "suppress", "rival-pit" };
                    return Voice("casual", Det(EventTypes.RivalPit, data));
                }
                case "tyre_hot":
                {
                    double d = Dbl(a, "delta");
                    if (d < SpeechDomain.TyreHotMin)
                        return new[] { "none" };
                    int w = Array.IndexOf(Wheels, Str(a, "wheel"));
                    Dictionary<string, object> data = TyreAnalyzer.HotEvent(
                        w < 2 ? "front_lr" : "rear_lr", w % 2 == 1 ? d : -d, SpeechDomain.TyreHotMin);
                    if (data == null)
                        return new[] { "suppress", "tyre-hot" };
                    return Voice("casual", Det(EventTypes.TyreWarning, data));
                }
                case "tyre_wear":
                {
                    var worst = new Dictionary<string, object>
                    {
                        { "wheel", Str(a, "wheel") }, { "remaining", 0.3 },
                        { "laps_left", Dbl(a, "laps_left") },
                    };
                    Dictionary<string, object> data = TyreAnalyzer.WearEvent(worst, 0.35);
                    if (data == null)
                        return new[] { "suppress", "tyre-wear" };
                    return Voice("casual", Det(EventTypes.TyreWarning, data));
                }
                case "lap_analysis":
                {
                    JsonElement pw = a.GetProperty("pit_window_laps");
                    bool none = pw.ValueKind == JsonValueKind.Null;
                    var data = new Dictionary<string, object>();
                    data["pit_window_laps"] = none ? null : (object)pw.GetInt32();
                    string text = Det(EventTypes.LapAnalysis, data);
                    if (text != null)
                        return Voice("casual", text);
                    return none ? new[] { "none" } : new[] { "suppress", "pit-window" };
                }
                case "milestone":
                    return Voice("casual", Det(EventTypes.RaceMilestone, a));
                case "fixed":
                {
                    int i = Int(a, "index");
                    if (i >= fixedTexts.Count)
                        return new[] { "none" };
                    return Voice(fixedTexts[i].Key, fixedTexts[i].Value);
                }
                case "penalty":
                {
                    string[] pen = RaceControlAnalyzer.ParsePenalty(Str(a, "text"));
                    if (pen == null)
                        return new[] { "none" };
                    return Voice("urgent", Messages.PenaltyText(pen[0], pen[1]));
                }
                case "fuel_save":
                {
                    double fuelL = Dbl(a, "fuel_l"), burn = Dbl(a, "burn"), save = Dbl(a, "save_delta");
                    int left = Int(a, "left");
                    double target = fuelL / left;
                    if (burn - target < save)
                        return new[] { "none" };
                    string text = FuelAnalyzer.SaveMessage(fuelL, burn, left, save);
                    return text != null ? Voice("casual", text) : new[] { "suppress", "fuel-save" };
                }
                case "damage_report":
                {
                    bool needBox;
                    List<string> problems;
                    string text = HealthAnalyzer.DamageReportMessage(
                        Bools(a, "detached"), Bools(a, "flat"), Bools(a, "losing"), Bytes(a, "dents"),
                        out needBox, out problems);
                    return Voice("casual", text);
                }
                case "wing":
                {
                    double d = Dbl(a, "drop_m");
                    if (d < 0.010)
                        return new[] { "none" };
                    string text = HealthAnalyzer.WingMessage(d);
                    return text != null ? Voice("casual", text) : new[] { "suppress", "wing" };
                }
                case "brake":
                {
                    double avg = Dbl(a, "avg");
                    if (avg < SpeechDomain.BrakeTMin)
                        return new[] { "none" };
                    string text = HealthAnalyzer.BrakeMessage(avg);
                    return text != null ? Voice("casual", text) : new[] { "suppress", "brake" };
                }
                case "repair":
                {
                    double d = Dbl(a, "delta");
                    if (d < SpeechDomain.RepairMin)
                        return new[] { "none" };
                    return Voice("casual", HealthAnalyzer.RepairMessage(d));
                }
                case "traffic_multi":
                {
                    var cars = new List<TrafficAnalyzer.MultiCar>();
                    foreach (JsonElement car in a.GetProperty("cars").EnumerateArray())
                    {
                        cars.Add(new TrafficAnalyzer.MultiCar
                        {
                            State = car[0].GetString(),
                            Cls = car[1].GetString(),
                            Side = car[2].ValueKind == JsonValueKind.Null ? null : car[2].GetString(),
                        });
                    }
                    return Voice("urgent", TrafficAnalyzer.ComposeMulti(cars));
                }
                case "briefing":
                {
                    var b = new SessionBriefer.BriefingInputs
                    {
                        SessionType = Int(a, "session_type"),
                        Track = Str(a, "track"),
                        EndEt = Dbl(a, "end_et"),
                        CurEt = Dbl(a, "cur_et"),
                        MaxLaps = Int(a, "max_laps"),
                        MidJoin = a.GetProperty("mid_join").GetBoolean(),
                        ClsCount = Int(a, "cls_count"),
                        ClassPlace = Int(a, "class_place"),
                        TrackTemp = Dbl(a, "track_temp"),
                        Raining = Dbl(a, "raining"),
                        Fuel = Dbl(a, "fuel"),
                        StartSpotterSec = Dbl(a, "start_spotter_sec"),
                    };
                    return Voice("casual", SessionBriefer.ComposeText(b, packOnly: true));
                }
                case "lap_time":
                {
                    double t = Dbl(a, "lap_time");
                    string text = StatusReporter.LaptimeText(t, a.GetProperty("best").GetBoolean());
                    if (!SpeechDomain.LaptimeInPack(t))
                        return new[] { "drop", "casual", text, "laptime-range" };
                    return Voice("casual", text);
                }
                case "status":
                {
                    double? fuel = NDbl(a, "fuel_laps");
                    string text = StatusReporter.StatusText(Int(a, "class_place"),
                        Dbl(a, "gap_ahead"), Dbl(a, "gap_behind"), fuel,
                        NDbl(a, "tyre_left"), a.GetProperty("has_tyre").GetBoolean());
                    if (text != null)
                        return Voice("casual", text);
                    // 도메인 밖 값만 있어서 전부 빠졌으면 억제, 원래 말할 게 없었으면 NONE
                    if (Int(a, "class_place") > SpeechDomain.ClassPosMax
                        || (fuel.HasValue && !SpeechDomain.StatusFuelOk(fuel.Value)))
                        return new[] { "suppress", "status" };
                    return new[] { "none" };
                }
                case "test_speak":
                    return Voice("casual", pool.Lines("pit_call", "casual")[0]);
                default:
                    throw new InvalidOperationException("알 수 없는 kind: " + kind);
            }
        }

        private static string Det(string type, JsonElement a)
        {
            return Det(type, Dict(a));
        }

        private static string Det(string type, Dictionary<string, object> data)
        {
            var renderer = new VoiceRenderer(new PhrasePool(new Random(0)));
            return renderer.RenderDeterministic(new RadioEvent { Type = type, Data = data });
        }

        private static Dictionary<string, object> Dict(JsonElement obj)
        {
            var d = new Dictionary<string, object>();
            foreach (JsonProperty p in obj.EnumerateObject())
                d[p.Name] = Value(p.Value);
            return d;
        }

        private static object Value(JsonElement v)
        {
            switch (v.ValueKind)
            {
                case JsonValueKind.String:
                    return v.GetString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                    return null;
                case JsonValueKind.Number:
                {
                    string raw = v.GetRawText();
                    if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
                    {
                        long l = v.GetInt64();
                        return l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l;
                    }
                    return v.GetDouble();
                }
                default:
                    return v.GetRawText();
            }
        }

        private static string Str(JsonElement a, string name)
        {
            JsonElement v = a.GetProperty(name);
            return v.ValueKind == JsonValueKind.Null ? null : v.GetString();
        }

        private static int Int(JsonElement a, string name)
        {
            return a.GetProperty(name).GetInt32();
        }

        private static double Dbl(JsonElement a, string name)
        {
            JsonElement v = a.GetProperty(name);
            if (v.ValueKind != JsonValueKind.String)
                return v.GetDouble();
            // 비유한 값은 JSON에 없어 문자열로 싣는다 (파이썬 float("nan"/"inf"/"-inf"))
            switch (v.GetString())
            {
                case "nan": return double.NaN;
                case "inf": return double.PositiveInfinity;
                case "-inf": return double.NegativeInfinity;
                default: return double.Parse(v.GetString(), CultureInfo.InvariantCulture);
            }
        }

        private static double? NDbl(JsonElement a, string name)
        {
            JsonElement v = a.GetProperty(name);
            return v.ValueKind == JsonValueKind.Null ? (double?)null : v.GetDouble();
        }

        private static bool[] Bools(JsonElement a, string name)
        {
            var list = new List<bool>();
            foreach (JsonElement v in a.GetProperty(name).EnumerateArray())
                list.Add(v.GetBoolean());
            return list.ToArray();
        }

        private static byte[] Bytes(JsonElement a, string name)
        {
            var list = new List<byte>();
            foreach (JsonElement v in a.GetProperty(name).EnumerateArray())
                list.Add((byte)v.GetInt32());
            return list.ToArray();
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");
        }
    }
}
