using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TeamRadio56.Core.Engine;
using TeamRadio56.Core.Logic;
using TeamRadio56.Core.Update;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// --voice-selftest: 한 목소리 정책 — 세션 목소리로 낼 수 없는 콜은 다른 목소리
    /// (edge/Windows 음성)로 새지 않고 무음(자막만)이 되는지. 임시 폴더 픽스처와
    /// 가짜 재생기·edge 합성·SAPI·시계로 돈다 (네트워크·오디오 장치 없음).
    /// --dump-voice-table / --pack-state-check는 파이썬 tools/voice_policy_selftest.py와 대조.
    /// </summary>
    internal static class VoiceSelfTest
    {
        private static int _pass;
        private static readonly List<string> Failures = new List<string>();
        private static string _root;

        private const string EdgeVoice = "en-GB-RyanNeural";
        private const int Rate = 10;
        private const string TextA = "Box box, box box.";              // _rfx3 통문장
        private const string TextB = "Alpha one check. Bravo two check.";  // 세그먼트 2개 + 자산
        private const string TextC = "Charlie raw only.";              // 원본 kokoro wav
        private const string TextD = "Delta edge only.";               // edge mp3만
        private const string TextE = "Echo nothing here.";             // 아무것도 없음

        public static int Run()
        {
            Messages.SetLanguage("en");
            _root = Path.Combine(Path.GetTempPath(), "teamradio56-voicetest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try
            {
                Section("kokoro 세션 — 팩 소스만, 미스는 무음", KokoroOnly);
                Section("kokoro 세션 — 무전 효과 설정 꺼도 효과본", KokoroIgnoresFxSetting);
                Section("edge 세션 — kokoro 파일을 안 보고, 오프라인이면 무음", EdgeOnly);
                Section("ko — edge, Windows 음성 없음", Korean);
                Section("세션 목소리 결정표", KindTable);
                Section("목소리 섞임 허용 — edge → Windows 음성", AllowFallback);
                Section("Stop — 끊긴 재생은 무음도 대체도 아님", Stops);
                Section("재생 게이트 — 두 발화기도 한 줄로", Gate);
                Section("보이스 워커 — 최근 무전에 무음 표시", Worker);
                Section("상태 파일 call{i}_dropped", StatusParse);
            }
            finally
            {
                try { Directory.Delete(_root, true); } catch (Exception) { }
            }

            Console.WriteLine();
            if (Failures.Count > 0)
            {
                Console.WriteLine($"❌ 한 목소리 정책 셀프테스트 실패 {Failures.Count}건 (통과 {_pass}건)");
                foreach (string f in Failures)
                    Console.WriteLine("   - " + f);
                return 1;
            }
            Console.WriteLine($"✅ 한 목소리 정책 셀프테스트 {_pass}건 통과");
            return 0;
        }

        // -- 대조 도구 ---------------------------------------------------------

        /// <summary>세션 목소리 결정표 — engine × lang × pack (파이썬 --dump-table과 diff).</summary>
        public static int DumpTable(string outPath)
        {
            var sb = new StringBuilder();
            foreach (string engine in new[] { "kokoro", "edge" })
                foreach (string lang in new[] { "en", "ko" })
                    foreach (string pack in new[] { VoicePolicy.PackOk, VoicePolicy.PackPartial,
                                                    VoicePolicy.PackIncomplete, VoicePolicy.PackNone })
                    {
                        string note;
                        VoiceKind kind = VoicePolicy.ResolveKind(engine, lang == "ko", pack, false, out note);
                        sb.Append(engine).Append('\t').Append(lang).Append('\t').Append(pack).Append('\t')
                          .Append(VoicePolicy.KindName(kind)).Append('\t').Append(note ?? "").Append('\n');
                    }
            if (outPath == null)
                Console.Write(sb.ToString());
            else
                File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            return 0;
        }

        /// <summary>파이썬이 만든 픽스처 폴더들의 팩 상태가 expected.tsv와 같은지.</summary>
        public static int PackStateCheck(string dir)
        {
            string expected = Path.Combine(dir, "expected.tsv");
            if (!File.Exists(expected))
            {
                Console.Error.WriteLine("expected.tsv 없음: " + dir);
                return 2;
            }
            int n = 0, bad = 0;
            foreach (string line in File.ReadAllLines(expected, Encoding.UTF8))
            {
                if (line.Length == 0)
                    continue;
                string[] f = line.Split('\t');
                string sub = Path.Combine(dir, f[0]);
                var probe = new VoiceCache(sub, EdgeVoice, Rate, "bm_george", true, "kokoro");
                string state = VoicePolicy.KokoroPackState(sub, probe);
                n++;
                if (state != f[1])
                {
                    bad++;
                    Console.WriteLine($"❌ 팩 상태 다름 — {f[0]}: 파이썬 {f[1]}, C# {state}");
                }
            }
            if (bad > 0 || n == 0)
                return 1;
            Console.WriteLine($"✅ 팩 상태 {n}종 파이썬과 일치");
            return 0;
        }

        // -- 픽스처 ------------------------------------------------------------

        private static string NewFixture()
        {
            string dir = Path.Combine(_root, "c" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            var names = new VoiceCache(dir, EdgeVoice, Rate);
            string kokoroA = names.CandidateFileNames(TextA, "casual").First();
            WriteWav(Path.Combine(dir, Path.ChangeExtension(kokoroA, null) + "_rfx3.wav"), 800);
            foreach (string s in Messages.SplitSentences(TextB))
                WriteWav(Path.Combine(dir, VoiceCache.SegmentFileName(s, "casual")), 400);
            foreach (string asset in RadioAssembler.AssetNames)
                WriteWav(Path.Combine(dir, asset), 80);
            WriteWav(Path.Combine(dir, names.CandidateFileNames(TextC, "casual").First()), 600);
            string edgeD = names.CandidateFileNames(TextD, "casual").Last();
            File.WriteAllBytes(Path.Combine(dir, edgeD), new byte[] { 0x49, 0x44, 0x33, 1, 2, 3 });
            return dir;
        }

        /// <summary>8 kHz 모노 PCM16 wav (조립기가 읽을 수 있는 실제 형식).</summary>
        private static void WriteWav(string path, int samples)
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            int data = samples * 2;
            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(36 + data);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(8000);
            w.Write(16000);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(data);
            for (int i = 0; i < samples; i++)
                w.Write((short)((i % 40) * 100 - 2000));
            w.Flush();
            File.WriteAllBytes(path, ms.ToArray());
        }

        // -- 가짜 -------------------------------------------------------------

        private sealed class FakePlayer : IRadioPlayer
        {
            public readonly List<string> Files = new List<string>();
            public int WavCalls;
            public bool Succeed = true;
            public RadioVoice StopDuringPlay;
            public int SleepMs;
            public static int Active;
            public static int MaxActive;

            public bool PlayFile(string path)
            {
                lock (Files)
                    Files.Add(path);
                return Play();
            }

            public bool PlayWav(byte[] wav)
            {
                Interlocked.Increment(ref WavCalls);
                return Play();
            }

            private bool Play()
            {
                int now = Interlocked.Increment(ref Active);
                int seen;
                while ((seen = MaxActive) < now
                       && Interlocked.CompareExchange(ref MaxActive, now, seen) != seen)
                {
                }
                if (SleepMs > 0)
                    Thread.Sleep(SleepMs);
                if (StopDuringPlay != null)
                    StopDuringPlay.Stop();
                Interlocked.Decrement(ref Active);
                return Succeed;
            }

            public int Calls { get { lock (Files) return Files.Count + WavCalls; } }

            public void Stop() { }
        }

        private sealed class FakeEdge
        {
            public int Calls;
            public bool Succeed = true;

            public bool Synth(string text, string voice, string rate, string pitch, string target)
            {
                Calls++;
                if (!Succeed)
                    return false;
                File.WriteAllBytes(target, new byte[] { 0x49, 0x44, 0x33 });
                return true;
            }
        }

        private sealed class FakeSapi : IFallbackSpeaker
        {
            public int Calls;

            public bool SayAndWait(string text)
            {
                Calls++;
                return true;
            }
        }

        private sealed class Clock
        {
            public DateTime Now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>플러그인 CreateRadio와 같은 조립 — Windows 음성을 넘길지는 RadioVoice.Create가
        /// 정한다 (가짜 SAPI는 항상 건네고, 쓰였는지를 센다).</summary>
        private static RadioVoice Radio(string dir, VoiceKind kind, bool allow, FakePlayer player,
                                        FakeEdge edge, FakeSapi sapi = null,
                                        Clock clock = null, bool radioFx = true, bool ko = false)
        {
            Clock c = clock ?? new Clock();
            VoiceRoute route = VoicePolicy.CreateRoute(dir, EdgeVoice, Rate, kind, radioFx, allow,
                                                       edge.Synth, () => c.Now);
            return RadioVoice.Create(route, player, allow, ko,
                                     sapi == null ? null : (Func<IFallbackSpeaker>)(() => sapi));
        }

        private static bool IsKokoroFile(string dir, string path, params string[] texts)
        {
            var names = new VoiceCache(dir, EdgeVoice, Rate);
            string file = Path.GetFileName(path);
            foreach (string t in texts)
            {
                string k = names.CandidateFileNames(t, "casual").First();
                if (file == k || file == Path.ChangeExtension(k, null) + "_rfx3.wav")
                    return true;
            }
            return file.EndsWith(VoiceCache.SegSuffix, StringComparison.Ordinal);
        }

        // -- 케이스 ------------------------------------------------------------

        private static void KokoroOnly()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            var edge = new FakeEdge();
            var sapi = new FakeSapi();
            RadioVoice radio = Radio(dir, VoiceKind.Kokoro, false, player, edge, sapi);

            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Played
                  && radio.LastSource == "KokoroFx", "A → KokoroFx 재생");
            Check(player.Files.Count == 1 && player.Files[0].EndsWith("_rfx3.wav"), "A는 _rfx3 파일");
            Check(radio.Speak(TextB, "casual", false) == SpeakResult.Played
                  && radio.LastSource == "KokoroSeg" && player.WavCalls == 1, "B → 세그먼트 조립 (PlayWav 1회)");
            Check(radio.Speak(TextC, "casual", false) == SpeakResult.Played
                  && radio.LastSource == "KokoroRaw", "C → 원본 통문장");
            int before = player.Calls;
            Check(radio.Speak(TextD, "casual", false) == SpeakResult.Dropped
                  && radio.LastReason == "kokoro-miss", "D(edge mp3만) → 무음 kokoro-miss");
            Check(radio.Speak(TextE, "urgent", true) == SpeakResult.Dropped, "E → 무음");
            Check(radio.Speak(TextE, "urgent", true) == SpeakResult.Dropped, "E 다시 → 무음");
            Check(player.Calls == before, "무음 콜은 재생기에 안 간다");
            Check(!player.Files.Any(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)),
                  "kokoro 세션은 edge mp3를 재생하지 않는다");
            Check(edge.Calls == 0 && sapi.Calls == 0, "edge 합성·SAPI 0회");
            Check(radio.Drops.Count == 3 && radio.Drops.Distinct == 2, "무음 3건, 서로 다른 텍스트 2종");

            var failing = new FakePlayer { Succeed = false };
            RadioVoice r2 = Radio(dir, VoiceKind.Kokoro, false, failing, edge, sapi);
            Check(r2.Speak(TextA, "casual", false) == SpeakResult.Dropped && r2.LastReason == "play-failed",
                  "재생 실패 → 무음 play-failed");
            Check(failing.Calls == 1 && sapi.Calls == 0, "재생 실패를 다른 소스로 다시 시도하지 않는다");
        }

        private static void KokoroIgnoresFxSetting()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, VoiceKind.Kokoro, false, player, new FakeEdge(), radioFx: false);
            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Played
                  && player.Files.Count == 1 && player.Files[0].EndsWith("_rfx3.wav"),
                  "무전 효과 설정 꺼짐이어도 kokoro는 _rfx3");
            Check(radio.Speak(TextB, "casual", false) == SpeakResult.Played && player.WavCalls == 1,
                  "무전 효과 설정 꺼짐이어도 세그먼트 조립");
        }

        private static void EdgeOnly()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            var edge = new FakeEdge();
            var clock = new Clock();
            var sapi = new FakeSapi();
            RadioVoice radio = Radio(dir, VoiceKind.Edge, false, player, edge, sapi, clock);

            foreach (string t in new[] { TextA, TextB, TextC })
                Check(radio.Speak(t, "casual", false) == SpeakResult.Played && radio.LastSource == "EdgeLive",
                      "edge 세션: " + t + " → 런타임 합성");
            Check(player.WavCalls == 0 && player.Files.All(f => f.EndsWith(".mp3") && !IsKokoroFile(dir, f, TextA, TextB, TextC)),
                  "edge 세션은 kokoro 파일·세그먼트를 재생하지 않는다");
            int synth = edge.Calls;
            Check(radio.Speak(TextD, "casual", false) == SpeakResult.Played && radio.LastSource == "EdgeCache"
                  && edge.Calls == synth, "D → 캐시된 edge mp3, 합성 없음");
            edge.Succeed = false;
            Check(radio.Speak(TextE, "casual", false) == SpeakResult.Dropped && radio.LastReason == "edge-failed",
                  "합성 실패 → 무음 edge-failed");
            synth = edge.Calls;
            Check(radio.Speak("Foxtrot never cached.", "casual", false) == SpeakResult.Dropped
                  && radio.LastReason == "edge-offline" && edge.Calls == synth,
                  "2분 안의 다음 미스 → 합성 시도 없이 edge-offline");
            clock.Now = clock.Now.AddSeconds(121);
            radio.Speak("Golf after block.", "casual", false);
            Check(edge.Calls == synth + 1, "121초 뒤 → 다시 합성 시도");
            Check(sapi.Calls == 0, "edge 세션(섞임 비허용)은 SAPI 0회");
        }

        private static void Korean()
        {
            string note;
            Check(VoicePolicy.ResolveKind("kokoro", true, VoicePolicy.PackOk, false, out note) == VoiceKind.Edge
                  && note == VoicePolicy.NoteKo, "ko + kokoro → edge (ko)");
            Check(!VoicePolicy.FallbackAllowed(true, true), "ko는 설정을 켜도 Windows 음성 없음");
            Check(VoicePolicy.FallbackAllowed(true, false) && !VoicePolicy.FallbackAllowed(false, false),
                  "en은 설정을 켰을 때만 Windows 음성");
            string dir = NewFixture();
            var sapi = new FakeSapi();
            var edge = new FakeEdge { Succeed = false };
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, VoiceKind.Edge, true, player, edge, sapi, ko: true);
            Check(radio.Speak("한국어 콜.", "casual", false) == SpeakResult.Dropped && sapi.Calls == 0,
                  "ko edge 실패 → 무음, SAPI 0회 (설정 켜짐)");
        }

        private static void KindTable()
        {
            string note;
            Check(VoicePolicy.ResolveKind("kokoro", false, VoicePolicy.PackNone, false, out note) == VoiceKind.Edge
                  && note == VoicePolicy.NoteNoKokoro, "팩 없음 → edge (no-kokoro)");
            Check(VoicePolicy.ResolveKind("kokoro", false, VoicePolicy.PackIncomplete, false, out note) == VoiceKind.Edge
                  && note == VoicePolicy.NotePackIncomplete, "설치 끊김 → edge (pack-incomplete)");
            Check(VoicePolicy.ResolveKind("kokoro", false, VoicePolicy.PackPartial, false, out note) == VoiceKind.Kokoro
                  && note == VoicePolicy.NotePackPartial, "설치 끊김이지만 클립 남음 → kokoro (pack-partial)");
            Check(Messages.IsKoLanguage("ko-KR") && Messages.IsKoLanguage("KO") && !Messages.IsKoLanguage("en")
                  && !Messages.IsKoLanguage(null), "멘트 언어 ko 판정 = 파이썬 startswith(\"ko\")");
            Check(VoicePolicy.ResolveKind("kokoro", Messages.IsKoLanguage("ko-KR"), VoicePolicy.PackOk, false,
                                          out note) == VoiceKind.Edge && note == VoicePolicy.NoteKo,
                  "ko-KR + kokoro → edge (ko)");
            Check(VoicePolicy.ResolveKind("kokoro", false, VoicePolicy.PackOk, false, out note) == VoiceKind.Kokoro
                  && note == null, "팩 있음 → kokoro");
            Check(VoicePolicy.ResolveKind("edge", false, VoicePolicy.PackOk, false, out note) == VoiceKind.Edge
                  && note == null, "edge 선택 → edge");
            Check(VoicePolicy.ResolveKind("kokoro", false, VoicePolicy.PackNone, true, out note) == VoiceKind.Kokoro,
                  "런타임 합성 가능 → kokoro");
            Check(VoicePolicy.ProbeText() == "Box box, box box.", "팩 확인 문장 = pit_call 첫 줄");
            string dir = NewFixture();
            var probe = new VoiceCache(dir, EdgeVoice, Rate, "bm_george", true, "kokoro");
            Check(VoicePolicy.KokoroPackState(dir, probe) == VoicePolicy.PackOk, "자산 4개 → ok (옛 v015)");
            AudioPack.WriteMarker(dir, AudioPack.IncompletePrefix + "kokoro-v016");
            Check(VoicePolicy.KokoroPackState(dir, probe) == VoicePolicy.PackPartial,
                  "설치 끊김 표식 + 확인용 통문장 남음 → partial");
            Check(VoicePolicy.KokoroPackState(Path.Combine(_root, "nope"), probe) == VoicePolicy.PackNone,
                  "폴더 없음 → none");
        }

        private static void AllowFallback()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            var edge = new FakeEdge();
            var sapi = new FakeSapi();
            RadioVoice radio = Radio(dir, VoiceKind.Kokoro, true, player, edge, sapi);
            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Played && radio.LastSource == "KokoroFx",
                  "허용이어도 팩에 있으면 kokoro");
            Check(radio.Speak(TextD, "casual", false) == SpeakResult.Played && radio.LastSource == "EdgeCache",
                  "허용: D → 캐시된 edge");
            Check(radio.Speak(TextE, "casual", false) == SpeakResult.Played && radio.LastSource == "EdgeLive",
                  "허용: E → edge 합성");
            edge.Succeed = false;
            Check(radio.Speak("Hotel offline.", "casual", false) == SpeakResult.Played
                  && radio.LastSource == "sapi" && sapi.Calls == 1, "허용: 합성 실패 → Windows 음성 1회");
            Check(radio.Drops.Count == 0, "허용이면 무음 기록 없음");
        }

        private static void Stops()
        {
            string dir = NewFixture();
            var sapi = new FakeSapi();
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, VoiceKind.Kokoro, true, player, new FakeEdge(), sapi);
            radio.Stop();
            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Stopped
                  && player.Calls == 0 && sapi.Calls == 0, "Stop 뒤 Speak → Stopped, 재생·SAPI 0");

            var p2 = new FakePlayer();
            RadioVoice r2 = Radio(dir, VoiceKind.Kokoro, false, p2, new FakeEdge());
            p2.StopDuringPlay = r2;
            Check(r2.Speak(TextA, "casual", false) == SpeakResult.Stopped && r2.Drops.Count == 0,
                  "재생 중 Stop(true) → Stopped, 무음 기록 아님");

            var p3 = new FakePlayer { Succeed = false };
            var sapi3 = new FakeSapi();
            RadioVoice r3 = Radio(dir, VoiceKind.Kokoro, true, p3, new FakeEdge(), sapi3);
            p3.StopDuringPlay = r3;
            Check(r3.Speak(TextA, "casual", false) == SpeakResult.Stopped && sapi3.Calls == 0,
                  "Stop 뒤 재생 false → Stopped, SAPI 없음");
        }

        private static void Gate()
        {
            string dir = NewFixture();
            FakePlayer.Active = 0;
            FakePlayer.MaxActive = 0;
            var p1 = new FakePlayer { SleepMs = 15 };
            var p2 = new FakePlayer { SleepMs = 15 };
            RadioVoice r1 = Radio(dir, VoiceKind.Kokoro, false, p1, new FakeEdge());
            RadioVoice r2 = Radio(dir, VoiceKind.Kokoro, false, p2, new FakeEdge());
            var t1 = new Thread(() => { for (int i = 0; i < 10; i++) r1.Speak(TextA, "casual", false); });
            var t2 = new Thread(() => { for (int i = 0; i < 10; i++) r2.Speak(TextC, "casual", false); });
            t1.Start();
            t2.Start();
            t1.Join();
            t2.Join();
            Check(p1.Calls == 10 && p2.Calls == 10, "두 발화기 모두 재생");
            Check(FakePlayer.MaxActive == 1, "동시 재생 최대 1 (실제 " + FakePlayer.MaxActive + ")");
        }

        private static void Worker()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, VoiceKind.Kokoro, false, player, new FakeEdge());
            var bus = new EventBus(new Dictionary<string, double> { { "default", 0.0 } });
            var worker = new VoiceWorker(bus, radio);
            worker.Start();
            try
            {
                bus.Push(new RadioEvent { Type = "session_briefing", DedupKey = "a", Message = TextA });
                bus.Push(new RadioEvent { Type = "session_briefing", DedupKey = "e", Message = TextE });
                RecentCall[] calls = new RecentCall[0];
                for (int i = 0; i < 60; i++)
                {
                    calls = worker.RecentCallItems();
                    if (calls.Length == 2 && radio.Drops.Count == 1)
                        break;
                    Thread.Sleep(50);
                }
                RecentCall a = calls.FirstOrDefault(c => c.Text == TextA);
                RecentCall e = calls.FirstOrDefault(c => c.Text == TextE);
                Check(a != null && !a.Dropped, "A는 최근 무전에 재생으로");
                Check(e != null && e.Dropped, "E는 최근 무전에 (무음) 표시");
                Check(worker.RecentCalls().Any(l => l.EndsWith("  " + TextE)), "RecentCalls는 그대로 \"시각  텍스트\"");
            }
            finally
            {
                radio.Stop();
                worker.Dispose();
            }
        }

        private static void StatusParse()
        {
            string path = Path.Combine(_root, "status.txt");
            File.WriteAllText(path,
                "# t\ncall1 = 12:00:01  Box box, box box.\ncall2 = 12:00:05  Echo nothing here.\n"
                + "call2_dropped = true\nvoice = kokoro\nvoice_note = \nvoice_dropped = 3\n"
                + "voice_missing = 7\nllm = true\nllm_voice = false\n", new UTF8Encoding(false));
            var st = new EngineStatus();
            st.Refresh(path);
            RecentCall[] calls = st.RecentCallItems();
            Check(calls.Length == 2 && !calls[0].Dropped && calls[1].Dropped
                  && calls[1].Text == TextE && calls[1].Time == "12:00:05", "call{i}_dropped 파싱");
            Check(st.Voice == "kokoro" && st.VoiceNote == null && st.VoiceDropped == 3 && st.VoiceMissing == 7
                  && st.LlmOn == true && st.LlmVoice == false, "목소리 상태 키 파싱");
        }

        // -- 공통 --------------------------------------------------------------

        private static void Section(string name, Action body)
        {
            int before = Failures.Count;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Failures.Add(name + ": 예외 " + ex.GetType().Name + " — " + ex.Message);
            }
            Console.WriteLine((Failures.Count == before ? "  ✓ " : "  ✗ ") + name);
        }

        private static void Check(bool cond, string what)
        {
            if (cond)
                _pass++;
            else
                Failures.Add(what);
        }
    }
}
