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
    /// 파이썬과 같은 판단을 한다는 뜻 (변형 풀 랜덤 멘트는 비교 대상 아님 —
    /// 판단·타이밍·데이터, 그리고 결정적 템플릿 문장 "say"가 비교 대상).
    ///
    /// 사용법: dotnet run -- --replay race.jsonl[.gz] [--out calls.jsonl]
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            string replayPath = null;
            string outPath = null;
            string raceEndTracePath = null;
            bool dumpPregen = false;
            bool dumpCacheNames = false;
            string checkCacheDir = null;
            bool gec = false;
            string synthText = null;
            bool coverage = false;
            bool dumpSentences = false;
            bool dumpSegNames = false;
            int maxTier = 2;
            string tracksPath = null;
            string assembleDir = null;
            double minPct = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--replay" && i + 1 < args.Length)
                    replayPath = args[++i];
                else if (args[i] == "--out" && i + 1 < args.Length)
                    outPath = args[++i];
                else if (args[i] == "--race-end-trace" && i + 1 < args.Length)
                    raceEndTracePath = args[++i];
                else if (args[i] == "--lang" && i + 1 < args.Length)
                    Messages.SetLanguage(args[++i]);   // 분석기/풀 생성 전에
                else if (args[i] == "--dump-pregen")
                    dumpPregen = true;
                else if (args[i] == "--dump-cache-names")
                    dumpCacheNames = true;
                else if (args[i] == "--check-cache" && i + 1 < args.Length)
                    checkCacheDir = args[++i];
                else if (args[i] == "--fixed-check" && i + 1 < args.Length)
                    return FixedCheck(args[++i], outPath ?? FindOut(args));
                else if (args[i] == "--update-selftest")
                    return UpdateSelfTest.Run();
                else if (args[i] == "--release-check" && i + 1 < args.Length)
                    return ReleaseCheck.Run(args[++i],
                        ArgValue(args, "--plugin-source")
                            ?? "simhub/src/TeamRadio56.SimHub/TeamRadio56Plugin.cs",
                        ArgValue(args, "--manifest"));
                else if (args[i] == "--gec")
                    gec = true;
                else if (args[i] == "--coverage")
                    coverage = true;
                else if (args[i] == "--synth" && i + 1 < args.Length)
                    synthText = args[++i];
                else if (args[i] == "--dump-sentences")
                    dumpSentences = true;
                else if (args[i] == "--dump-seg-names")
                    dumpSegNames = true;
                else if (args[i] == "--tier" && i + 1 < args.Length)
                    maxTier = int.Parse(args[++i], CultureInfo.InvariantCulture);
                else if (args[i] == "--tracks" && i + 1 < args.Length)
                    tracksPath = args[++i];
                else if (args[i] == "--assemble-check" && i + 1 < args.Length)
                    assembleDir = args[++i];
                else if (args[i] == "--min" && i + 1 < args.Length)
                    minPct = double.Parse(args[++i], CultureInfo.InvariantCulture);
            }
            if (gec)
            {
                // 파이썬 edge_tts.DRM.generate_sec_ms_gec와 대조용
                Console.WriteLine(EdgeTtsClient.GenerateSecMsGec());
                return 0;
            }
            if (synthText != null)
            {
                string dst = outPath ?? "synth_test.mp3";
                bool ok = EdgeTtsClient.Synthesize(
                    synthText, "en-GB-RyanNeural", "+10%", "-6Hz", dst);
                Console.WriteLine(ok
                    ? "OK " + new FileInfo(dst).Length + " bytes → " + dst
                    : "FAIL: " + (EdgeTtsClient.LastError ?? "?"));
                return ok ? 0 : 1;
            }
            if (dumpPregen)
                return DumpPregen(outPath);
            if (dumpCacheNames)
                return DumpCacheNames(outPath);
            List<string> tracks = tracksPath != null
                ? PregenSentences.LoadTracks(tracksPath) : new List<string>();
            if (dumpSentences || dumpSegNames)
                return DumpSentences(outPath, maxTier, tracks, dumpSegNames);
            if (checkCacheDir != null)
                return CheckCache(checkCacheDir, maxTier, tracks);
            if (assembleDir != null)
                return AssembleCheck(assembleDir);
            if (replayPath == null)
            {
                Console.Error.WriteLine(
                    "사용법: --replay <file.jsonl[.gz]> [--out calls.jsonl] [--lang en|ko]"
                    + " [--race-end-trace trace.jsonl]"
                    + " | --dump-pregen [--out f] | --dump-cache-names [--out f]"
                    + " | --dump-sentences|--dump-seg-names [--tier N] [--tracks f] [--out f]"
                    + " | --replay <f> --coverage [--tier N] [--tracks f] [--min 퍼센트]"
                    + " | --check-cache <오디오캐시폴더> [--tier N] [--tracks f]"
                    + " | --assemble-check <폴더>"
                    + " | --update-selftest"
                    + " | --release-check <audio_pack.txt> [--plugin-source f] [--manifest f]"
                    + " | --gec | --synth <텍스트>");
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
            var briefer = new SessionBriefer();   // StartSpotterSec 기본 45 = 파이썬 DEFAULTS

            var lines = new List<string>();
            var accepted = new List<RadioEvent>();
            // 'say'는 수락 시점 data로 렌더 — 파이썬 recording_push의 깊은 복사와 같은 시점
            var sayRenderer = new VoiceRenderer(new PhrasePool(new Random(7)));
            bus.Accepted = ev =>
            {
                lines.Add(Format(clock, ev, sayRenderer.RenderDeterministic(ev)));
                accepted.Add(ev);
            };

            var trace = new List<string>();
            int ticks = 0;
            foreach ((Snapshot snap, bool connected) in ReplayLoader.Read(replayPath))
            {
                if (!connected)
                    continue;
                clock = snap.T;
                // main.py on_snapshot 순서: 세션 브리핑 → 분석기 틱 → 상태 갱신(랩 완료 감지)
                briefer.MaybeBrief(state, snap, bus);
                traffic.OnTick(state, snap, bus);
                racecontrol.OnTick(state, snap, bus);
                rivals.OnTick(state, snap, bus);
                health.OnTick(state, snap, bus);
                bool cheqBefore = state.RaceEnd.ChequeredEt.HasValue;
                bool finBefore = state.RaceEnd.Finished;
                LapRecord lap = state.Update(snap);
                RaceEndTracker re = state.RaceEnd;
                if (state.IsRace && (lap != null || re.ChequeredEt.HasValue != cheqBefore
                                     || re.Finished != finBefore))
                {
                    trace.Add(DataJson(new Dictionary<string, object>
                    {
                        { "t", Math.Round(snap.T, 2) },
                        { "lap", snap.PlayerScoring().TotalLaps },
                        { "laps_left", re.LapsLeft.HasValue ? (object)re.LapsLeft.Value : null },
                        { "finished", re.Finished },
                        { "chequered", re.ChequeredEt.HasValue },
                    }));
                }
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

            if (raceEndTracePath != null)
            {
                using var tw = new StreamWriter(raceEndTracePath, false, new UTF8Encoding(false));
                foreach (string line in trace)
                    tw.WriteLine(line);
            }
            if (coverage)
                return Coverage(accepted, maxTier, tracks, minPct);

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
        /// 리플레이에서 실제로 나갈 발화 문장을 렌더링해 코코로로 나갈 수 있는
        /// 비율을 잰다 — 통문장 클립(KOKORO), 문장이 전부 세그먼트 목록에 있음
        /// (KOKORO-SEG), 아니면 miss(빠진 문장 출력). 세션 브리핑의 첫 문장
        /// '{track}.'만 빠진 건 "트랙 이름 추가"로 따로 보고하고 점수에서 뺀다
        /// (리플레이 트랙명은 목록에 없는 'Circuit de Test'). --min 미만이면 1.
        /// </summary>
        private static int Coverage(List<RadioEvent> accepted, int maxTier,
                                    List<string> tracks, double minPct)
        {
            var pregen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> item
                     in PregenTexts.Enumerate(new PhrasePool()))
                pregen.Add(item.Key + "\t" + item.Value);
            var sentences = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> item
                     in PregenSentences.Enumerate(maxTier, tracks))
                sentences.Add(item.Key + "\t" + item.Value);
            // 트랙명이 없을 때의 브리핑 첫 문장 — 고정 문장이라 트랙 미스로 빼지 않는다
            string radioFirst = Messages.SplitSentences(
                Messages.Get("brief_radio", "kind", "x"))[0];

            var renderer = new VoiceRenderer(new PhrasePool(new Random(7)));
            int spoken = 0, whole = 0, seg = 0;
            var trackMisses = new List<string>();
            var byType = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
            foreach (RadioEvent ev in accepted)
            {
                string text = renderer.TextFor(ev).Key;
                if (string.IsNullOrEmpty(text))
                    continue;
                string tone = ev.Tone ?? "casual";
                string kind;
                var missing = new List<string>();
                if (pregen.Contains(tone + "\t" + text))
                {
                    kind = "KOKORO";
                }
                else
                {
                    List<string> sents = Messages.SplitSentences(text);
                    string trackMiss = null;
                    for (int i = 0; i < sents.Count; i++)
                    {
                        if (sentences.Contains(tone + "\t" + sents[i]))
                            continue;
                        if (i == 0 && ev.Type == EventTypes.SessionBriefing
                            && sents[0] != radioFirst)
                            trackMiss = sents[0];
                        else
                            missing.Add(sents[i]);
                    }
                    if (trackMiss != null && !trackMisses.Contains(trackMiss))
                        trackMisses.Add(trackMiss);
                    kind = missing.Count > 0 ? "miss"
                        : trackMiss != null ? "TRACK" : "KOKORO-SEG";
                }
                Console.WriteLine("  " + kind.PadRight(11) + " " + ev.Type + " | " + text);
                foreach (string m in missing)
                    Console.WriteLine("      빠진 문장 [" + tone + "] " + m);
                if (kind == "TRACK")
                    continue;   // 트랙 이름만 빠짐 — 점수 제외
                spoken++;
                bool ok = kind != "miss";
                if (kind == "KOKORO") whole++;
                else if (ok) seg++;
                int[] c;
                if (!byType.TryGetValue(ev.Type, out c)) { c = new int[2]; byType[ev.Type] = c; }
                c[0]++; if (ok) c[1]++;
            }
            int hit = whole + seg;
            double pct = spoken > 0 ? 100.0 * hit / spoken : 100.0;
            Console.WriteLine($"발화 {spoken}건 중 코코로 {hit}건 ({pct:F0}%) — "
                + $"통문장 {whole}, 문장 조립 {seg}, 미스 {spoken - hit}");
            foreach (var kv in byType)
                Console.WriteLine($"  {kv.Key}: {kv.Value[1]}/{kv.Value[0]}");
            foreach (string t in trackMisses)
                Console.WriteLine("트랙 이름 추가 (점수 제외): " + t);
            // 나눗셈 없이 비교 — 100% 기준이 부동소수 오차로 떨어지지 않게
            if (hit * 100.0 < minPct * spoken)
            {
                Console.WriteLine($"❌ 커버리지 {pct:F1}% < 기준 {minPct}%");
                return 1;
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
        /// 문장 단위 사전 생성 대상을 "톤\t문장"(또는 --dump-seg-names면
        /// "톤\t세그먼트파일명\t문장") 정렬·중복 제거로 출력 — 파이썬
        /// dump_pregen.py --sentences / --seg-names와 diff한다.
        /// </summary>
        private static int DumpSentences(string outPath, int maxTier,
                                         List<string> tracks, bool segNames)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> item
                     in PregenSentences.Enumerate(maxTier, tracks))
            {
                set.Add(segNames
                    ? item.Key + "\t" + VoiceCache.SegmentFileName(item.Value, item.Key)
                      + "\t" + item.Value
                    : item.Key + "\t" + item.Value);
            }
            TextWriter writer = outPath != null
                ? new StreamWriter(outPath, false, new UTF8Encoding(false))
                : Console.Out;
            foreach (string line in set)
                writer.WriteLine(line);
            if (outPath != null)
            {
                writer.Dispose();
                Console.Error.WriteLine($"사전 생성 문장 {set.Count}개 → {outPath}");
            }
            return 0;
        }

        /// <summary>
        /// 세그먼트 조립 바이트 대조 — tools/assemble_check.py가 만든 합성 팩과
        /// messages.tsv("하위폴더\t톤\t멘트")를 읽어 줄 i마다 dir/cs/{i}.wav를 쓴다
        /// (미스면 쓰지 않음). replay-check.sh가 dir/py/{i}.wav와 cmp한다.
        /// </summary>
        private static int AssembleCheck(string dir)
        {
            string outDir = Path.Combine(dir, "cs");
            Directory.CreateDirectory(outDir);
            string[] rows = File.ReadAllLines(Path.Combine(dir, "messages.tsv"), Encoding.UTF8);
            int hits = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                string[] f = rows[i].Split('\t');
                // 런타임(AudioSink.TryAssemble)과 같은 경로 — 자산 확인 + 문장 조회 + 조립
                var cache = new VoiceCache(Path.Combine(dir, f[0]), "en-GB-RyanNeural", 10);
                string firstMissing;
                string[] paths = cache.ResolveSegments(f[2], f[1], out firstMissing);
                byte[] wav = paths != null ? RadioAssembler.Build(cache.CacheDir, f[1], paths) : null;
                if (wav == null)
                    continue;
                File.WriteAllBytes(Path.Combine(outDir, i + ".wav"), wav);
                hits++;
            }
            Console.Error.WriteLine($"C# 조립 {hits}/{rows.Length}건 → {outDir}");
            return 0;
        }

        /// <summary>
        /// 실제 오디오 캐시 폴더에 대해 사전 캐시 전 항목을 Resolve하고, en이면
        /// maxTier 이하 문장 세그먼트(_seg1.wav)와 조립 자산 4개도 요구한다 —
        /// builtin 모드가 배포 캐시를 몇 개나 찾는지 확인하는 진단 도구.
        /// </summary>
        private static int CheckCache(string cacheDir, int maxTier, List<string> tracks)
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
            bool ok = hit == total;

            // 문장 세그먼트 — ko는 열거가 비어 있어 자동으로 건너뜀
            int segTotal = 0, segHit = 0;
            var segMisses = new List<string>();
            foreach (KeyValuePair<string, string> item
                     in PregenSentences.Enumerate(maxTier, tracks))
            {
                segTotal++;
                if (File.Exists(Path.Combine(cacheDir,
                        VoiceCache.SegmentFileName(item.Value, item.Key))))
                {
                    segHit++;
                }
                else if (segMisses.Count < 5)
                {
                    segMisses.Add("  [" + item.Key + "] " + item.Value);
                }
            }
            if (segTotal > 0)
            {
                Console.WriteLine($"문장 세그먼트 {segHit}/{segTotal} (단계 ≤{maxTier})");
                if (segHit < segTotal)
                {
                    Console.WriteLine("세그먼트 미스 예시:");
                    foreach (string m in segMisses)
                        Console.WriteLine(m);
                    ok = false;
                }
                var missingAssets = new List<string>();
                foreach (string name in RadioAssembler.AssetNames)
                {
                    if (!File.Exists(Path.Combine(cacheDir, name)))
                        missingAssets.Add(name);
                }
                if (missingAssets.Count > 0)
                {
                    Console.WriteLine("조립 자산 없음: " + string.Join(", ", missingAssets));
                    ok = false;
                }
                else
                {
                    Console.WriteLine($"조립 자산 {RadioAssembler.AssetNames.Length}개 있음");
                }
            }
            return ok ? 0 : 1;
        }

        private static string FindOut(string[] args) => ArgValue(args, "--out");

        private static string ArgValue(string[] args, string name)
        {
            for (int j = 0; j + 1 < args.Length; j++)
                if (args[j] == name)
                    return args[j + 1];
            return null;
        }

        /// <summary>
        /// tools/fixed_check.py가 쓴 "자릿수\t값" 줄을 Messages.PyFixed로
        /// 표기 — 파이썬 f"{v:.Nf}" 기대 출력과 diff한다.
        /// </summary>
        private static int FixedCheck(string inPath, string outPath)
        {
            TextWriter writer = outPath != null
                ? new StreamWriter(outPath, false, new UTF8Encoding(false))
                : Console.Out;
            foreach (string line in File.ReadAllLines(inPath))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0)
                    continue;
                int digits = int.Parse(line.Substring(0, tab), CultureInfo.InvariantCulture);
                double v = double.Parse(line.Substring(tab + 1), NumberStyles.Float,
                                        CultureInfo.InvariantCulture);
                writer.WriteLine(Messages.PyFixed(v, digits));
            }
            if (outPath != null)
                writer.Dispose();
            return 0;
        }

        private static string Format(double t, RadioEvent ev, string say)
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
            AppendKv(sb, "say", say);
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
