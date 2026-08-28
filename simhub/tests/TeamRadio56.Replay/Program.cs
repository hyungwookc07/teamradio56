using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using TeamRadio56.Core.Logic;
using TeamRadio56.Core.Telemetry;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// 리플레이 회귀 러너 — 파이썬 tools/replay_calls.py와 짝.
    ///
    /// 녹화 JSONL을 C# 파이프라인(현재: 트래픽 분석기)에 먹이고, 이벤트
    /// 버스가 "수락한" 이벤트를 JSONL로 출력한다. 두 출력이 같으면 포팅이
    /// 파이썬과 같은 판단을 한다는 뜻 (멘트 문구/랜덤은 비교 대상 아님 —
    /// 판단·타이밍·데이터가 비교 대상).
    ///
    /// 사용법: dotnet run -- --replay race.jsonl[.gz] [--out calls.jsonl]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string replayPath = null;
            string outPath = null;
            bool dumpPregen = false;
            bool dumpCacheNames = false;
            string checkCacheDir = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--replay" && i + 1 < args.Length)
                    replayPath = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length)
                    outPath = args[++i];
                else if (args[i] == "--dump-pregen")
                    dumpPregen = true;
                else if (args[i] == "--dump-cache-names")
                    dumpCacheNames = true;
                else if (args[i] == "--check-cache" && i + 1 < args.Length)
                    checkCacheDir = args[++i];
                else if (args[i] == "--gec")
                {
                    // 파이썬 edge_tts.DRM.generate_sec_ms_gec와 대조용
                    Console.WriteLine(EdgeTtsClient.GenerateSecMsGec());
                    return 0;
                }
                else if (args[i] == "--synth" && i + 1 < args.Length)
                {
                    string text = args[++i];
                    string dst = outPath ?? "synth_test.mp3";
                    for (int j = i + 1; j + 1 < args.Length; j++)
                        if (args[j] == "--out") dst = args[j + 1];
                    bool ok = EdgeTtsClient.Synthesize(
                        text, "en-GB-RyanNeural", "+10%", "-6Hz", dst);
                    Console.WriteLine(ok
                        ? "OK " + new FileInfo(dst).Length + " bytes → " + dst
                        : "FAIL: " + (EdgeTtsClient.LastError ?? "?"));
                    return ok ? 0 : 1;
                }
            }
            if (dumpPregen)
                return DumpPregen(outPath);
            if (dumpCacheNames)
                return DumpCacheNames(outPath);
            if (checkCacheDir != null)
                return CheckCache(checkCacheDir);
            if (replayPath == null)
            {
                Console.Error.WriteLine(
                    "사용법: --replay <file.jsonl[.gz]> [--out calls.jsonl] | --dump-pregen [--out f]"
                    + " | --dump-cache-names [--out f] | --check-cache <오디오캐시폴더>");
                return 2;
            }

            double clock = 0.0;
            var bus = new EventBus(Cooldowns.Default(), () => clock);
            var state = new RaceState();
            var traffic = new TrafficAnalyzer(new TrafficSettings());
            var racecontrol = new RaceControlAnalyzer();
            var health = new HealthAnalyzer();
            var rivals = new RivalAnalyzer();
            var fuel = new FuelAnalyzer();
            var pace = new PaceAnalyzer();
            var tyres = new TyreAnalyzer();
            var strategy = new StrategyEngine();
            var reporter = new StatusReporter();

            var lines = new List<string>();
            bus.Accepted = ev => lines.Add(Format(clock, ev));

            int ticks = 0;
            foreach ((Snapshot snap, bool connected) in ReplayLoader.Read(replayPath))
            {
                if (!connected)
                    continue;
                clock = snap.T;
                // main.py on_snapshot 순서: 분석기 틱 → 상태 갱신(랩 완료 감지)
                traffic.OnTick(state, snap, bus);
                racecontrol.OnTick(state, snap, bus);
                rivals.OnTick(state, snap, bus);
                health.OnTick(state, snap, bus);
                LapRecord lap = state.Update(snap);
                if (lap != null)
                {
                    // main.py on_lap_complete 순서 그대로
                    Dictionary<string, object> fuelStatus = fuel.OnLap(state, snap, bus);
                    pace.OnLap(state, snap, bus, lap);
                    Dictionary<string, object> tyreStatus = tyres.OnLap(state, snap, bus);
                    if (lap.InPits && state.IsRace)
                    {
                        bus.Push(new RadioEvent
                        {
                            Type = EventTypes.StintBriefing,
                            Priority = Priority.Normal,
                            DedupKey = "stint_" + lap.LapNumber,
                        });
                    }
                    strategy.OnLap(state, snap, bus, fuelStatus, tyreStatus);
                    reporter.OnLap(state, snap, bus, fuelStatus, tyreStatus);
                    racecontrol.OnLap(state, snap, bus);
                    rivals.OnLap(state, snap, bus);
                    health.OnLap(state, snap, bus);
                }
                ticks++;
            }

            TextWriter writer = outPath != null
                ? new StreamWriter(outPath, false, new UTF8Encoding(false))
                : Console.Out;
            foreach (string line in lines)
                writer.WriteLine(line);
            if (outPath != null)
            {
                writer.Dispose();
                Console.Error.WriteLine(
                    $"틱 {ticks}개 처리, 수락 이벤트 {lines.Count}개 → {outPath}");
            }
            return 0;
        }

        /// <summary>
        /// 사전 캐시 대상 (톤, 텍스트) 전체를 "톤\t텍스트" 정렬·중복 제거로
        /// 출력 — 파이썬 tools/dump_pregen.py와 diff해 멘트 데이터와 슬롯
        /// 포매팅 포팅을 검증한다.
        /// </summary>
        private static int DumpPregen(string outPath)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            var pool = new TeamRadio56.Core.Logic.PhrasePool();
            foreach (KeyValuePair<string, string> item
                     in TeamRadio56.Core.Logic.PregenTexts.Enumerate(pool))
            {
                set.Add(item.Key + "\t" + item.Value);
            }
            TextWriter writer = outPath != null
                ? new StreamWriter(outPath, false, new UTF8Encoding(false))
                : Console.Out;
            foreach (string line in set)
                writer.WriteLine(line);
            if (outPath != null)
            {
                writer.Dispose();
                Console.Error.WriteLine($"사전 캐시 텍스트 {set.Count}개 → {outPath}");
            }
            return 0;
        }

        /// <summary>
        /// 사전 캐시 전 항목의 캐시 파일명(kokoro/edge)을 덤프 —
        /// 파이썬 dump_pregen.py --cache-names 출력과 diff해 파일명 규약
        /// (md5 앞 20자 등)이 어긋나지 않았는지 검증한다.
        /// 보이스/속도는 PluginSettings 기본값 고정.
        /// </summary>
        private static int DumpCacheNames(string outPath)
        {
            var cache = new VoiceCache("", "en-GB-RyanNeural", 10);
            var set = new SortedSet<string>(StringComparer.Ordinal);
            var pool = new TeamRadio56.Core.Logic.PhrasePool();
            foreach (KeyValuePair<string, string> item
                     in TeamRadio56.Core.Logic.PregenTexts.Enumerate(pool))
            {
                var names = new List<string>(cache.CandidateFileNames(item.Value, item.Key));
                set.Add(item.Key + "\t" + string.Join("\t", names) + "\t" + item.Value);
            }
            TextWriter writer = outPath != null
                ? new StreamWriter(outPath, false, new UTF8Encoding(false))
                : Console.Out;
            foreach (string line in set)
                writer.WriteLine(line);
            if (outPath != null)
            {
                writer.Dispose();
                Console.Error.WriteLine($"캐시 파일명 {set.Count}줄 → {outPath}");
            }
            return 0;
        }

        /// <summary>
        /// 실제 오디오 캐시 폴더에 대해 사전 캐시 전 항목을 Resolve —
        /// builtin 모드가 배포 캐시를 몇 개나 찾는지 확인하는 진단 도구.
        /// </summary>
        private static int CheckCache(string cacheDir)
        {
            var cache = new VoiceCache(cacheDir, "en-GB-RyanNeural", 10);
            if (!cache.Available)
            {
                Console.Error.WriteLine("캐시 폴더가 없습니다: " + cacheDir);
                return 2;
            }
            int total = 0, hit = 0, rfx = 0;
            var misses = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var pool = new TeamRadio56.Core.Logic.PhrasePool();
            foreach (KeyValuePair<string, string> item
                     in TeamRadio56.Core.Logic.PregenTexts.Enumerate(pool))
            {
                if (!seen.Add(item.Key + "\t" + item.Value))
                    continue;   // 파이썬 pregen과 같은 기준: (톤, 텍스트) 유일
                total++;
                string path = cache.Resolve(item.Value, item.Key);
                if (path == null)
                {
                    if (misses.Count < 5)
                        misses.Add("  [" + item.Key + "] " + item.Value);
                    continue;
                }
                hit++;
                if (path.EndsWith("_rfx3.wav", StringComparison.OrdinalIgnoreCase))
                    rfx++;
            }
            Console.WriteLine($"캐시 히트 {hit}/{total} (무전 효과본 {rfx}) — {cacheDir}");
            if (hit < total)
            {
                Console.WriteLine("미스 예시:");
                foreach (string m in misses)
                    Console.WriteLine(m);
            }
            return hit == total ? 0 : 1;
        }

        private static string Format(double t, RadioEvent ev)
        {
            // 파이썬 쪽과 같은 형식: 키 정렬된 JSON 한 줄
            var sb = new StringBuilder();
            sb.Append('{');
            AppendKv(sb, "data", DataJson(ev.Data), raw: true);
            sb.Append(',');
            AppendKv(sb, "key", ev.Key);
            sb.Append(',');
            AppendKv(sb, "message", ev.Message);
            sb.Append(',');
            AppendKv(sb, "prio", ((int)ev.Priority).ToString(CultureInfo.InvariantCulture), raw: true);
            sb.Append(',');
            AppendKv(sb, "t", Num(Math.Round(t, 2)), raw: true);
            sb.Append(',');
            AppendKv(sb, "tone", ev.Tone);
            sb.Append(',');
            AppendKv(sb, "type", ev.Type);
            sb.Append('}');
            return sb.ToString();
        }

        private static string DataJson(Dictionary<string, object> data)
        {
            var sb = new StringBuilder();
            WriteValue(sb, data);
            return sb.ToString();
        }

        /// <summary>파이썬 json.dumps(sort_keys=True, 구분자 컴팩트)와 동일 표기.</summary>
        private static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null)
            {
                sb.Append("null");
            }
            else if (v is string s)
            {
                sb.Append(Quote(s));
            }
            else if (v is int n)
            {
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
            }
            else if (v is double d)
            {
                sb.Append(Num(d));
            }
            else if (v is bool b)
            {
                sb.Append(b ? "true" : "false");
            }
            else if (v is Dictionary<string, object> dict)
            {
                var keys = new List<string>(dict.Keys);
                keys.Sort(StringComparer.Ordinal);
                sb.Append('{');
                for (int i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                        sb.Append(',');
                    sb.Append(Quote(keys[i])).Append(':');
                    WriteValue(sb, dict[keys[i]]);
                }
                sb.Append('}');
            }
            else if (v is System.Collections.IEnumerable seq)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in seq)
                {
                    if (!first)
                        sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
            }
            else
            {
                sb.Append(Quote(v.ToString()));
            }
        }

        private static void AppendKv(StringBuilder sb, string key, string value, bool raw = false)
        {
            sb.Append(Quote(key)).Append(':');
            if (value == null)
                sb.Append("null");
            else if (raw)
                sb.Append(value);
            else
                sb.Append(Quote(value));
        }

        private static string Num(double d)
        {
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture) + ".0";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string Quote(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
