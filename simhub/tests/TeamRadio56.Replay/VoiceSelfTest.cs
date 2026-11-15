using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TeamRadio56.Core.Logic;
using TeamRadio56.Core.Update;

namespace TeamRadio56.Replay
{
    /// <summary>
    /// --voice-selftest: 한 목소리 정책 — 플러그인의 목소리는 kokoro 음성 팩 하나뿐이다.
    /// 팩에 없는 콜, 팩이 없거나 설치가 끊긴 세션의 콜은 다른 목소리로 새지 않고
    /// 무음(자막만)이 되는지. 임시 폴더 픽스처와 가짜 재생기로 돈다 (오디오 장치 없음).
    /// --dump-voice-table / --pack-state-check는 파이썬 tools/voice_policy_selftest.py와 대조.
    /// </summary>
    internal static class VoiceSelfTest
    {
        private static int _pass;
        private static readonly List<string> Failures = new List<string>();
        private static string _root;

        private const string TextA = "Box box, box box.";              // _rfx3 통문장 (= 팩 확인 문장)
        private const string TextB = "Alpha one check. Bravo two check.";  // 세그먼트 2개 + 자산
        private const string TextC = "Charlie raw only.";              // 원본 kokoro wav
        private const string TextD = "Delta edge only.";               // 옛 edge mp3만 (예전 설치 잔여물)
        private const string TextE = "Echo nothing here.";             // 아무것도 없음
        private const string TextF = "Foxtrot fx only.";               // 확인 문장이 아닌 _rfx3 통문장 (남은 클립)

        public static int Run()
        {
            _root = Path.Combine(Path.GetTempPath(), "teamradio56-voicetest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            try
            {
                Section("음성 팩 세션 — 팩 소스만, 미스는 무음", KokoroOnly);
                Section("음성 팩 없음/설치 끊김 — 세션 전체 무음, 재생기 호출 없음", NoPack);
                Section("팩 상태 → 사유·사용 가능 여부", PackTable);
                Section("Stop — 끊긴 재생은 무음 기록이 아님", Stops);
                Section("재생 게이트 — 두 발화기도 한 줄로", Gate);
                Section("보이스 워커 — 최근 무전에 무음 표시", Worker);
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

        /// <summary>
        /// 팩 상태별 결정표 "pack\tvoice\tnote" (voice = kokoro | none). replay-check가 파이썬
        /// --dump-table의 kokoro·en 행(파이썬이 edge로 대신 말하는 자리는 none)과 diff한다.
        /// voice는 정책 함수가 아니라 실제 발화 경로의 결과 — 모든 클립이 있는 가상 폴더에
        /// 그 팩 상태로 만든 경로가 확인 문장을 재생하면 kokoro, 막으면 none.
        /// </summary>
        public static int DumpTable(string outPath)
        {
            var everything = new VoiceCache("pack", fileExists: _ => true);
            string probe = VoicePolicy.ProbeText();
            var sb = new StringBuilder();
            foreach (string pack in new[] { VoicePolicy.PackOk, VoicePolicy.PackPartial,
                                            VoicePolicy.PackIncomplete, VoicePolicy.PackNone })
            {
                bool speaks = VoicePolicy.RouteFor(everything, pack).Plan(probe, "casual").Source
                              != VoiceSource.None;
                sb.Append(pack).Append('\t')
                  .Append(speaks ? "kokoro" : "none").Append('\t')
                  .Append(VoicePolicy.PackNote(pack) ?? "").Append('\n');
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
                string state = VoicePolicy.KokoroPackState(sub, new VoiceCache(sub));
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

        private static string NewDir()
        {
            string dir = Path.Combine(_root, "c" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static string NewFixture()
        {
            string dir = NewDir();
            WriteWav(Path.Combine(dir, VoiceCache.WholeFxFileName(TextA, "casual")), 800);
            foreach (string s in Messages.SplitSentences(TextB))
                WriteWav(Path.Combine(dir, VoiceCache.SegmentFileName(s, "casual")), 400);
            foreach (string asset in RadioAssembler.AssetNames)
                WriteWav(Path.Combine(dir, asset), 80);
            WriteWav(Path.Combine(dir, VoiceCache.WholeFileName(TextC, "casual")), 600);
            WriteLegacyEdgeMp3(dir, TextD);
            return dir;
        }

        /// <summary>
        /// 팩이 아닌(또는 설치가 끊긴) 폴더에 남은 kokoro 클립 — 확인 문장이 아닌 _rfx3
        /// 통문장(F)과 원본 통문장(C), withSegments면 B의 세그먼트와 조립 자산도.
        /// 확인 문장(A)은 넣지 않는다.
        /// </summary>
        private static void WriteLeftovers(string dir, bool withSegments)
        {
            WriteWav(Path.Combine(dir, VoiceCache.WholeFxFileName(TextF, "casual")), 500);
            WriteWav(Path.Combine(dir, VoiceCache.WholeFileName(TextC, "casual")), 600);
            if (!withSegments)
                return;
            foreach (string s in Messages.SplitSentences(TextB))
                WriteWav(Path.Combine(dir, VoiceCache.SegmentFileName(s, "casual")), 400);
            foreach (string asset in RadioAssembler.AssetNames)
                WriteWav(Path.Combine(dir, asset), 80);
        }

        /// <summary>
        /// 예전 버전(edge 목소리)이 캐시에 남긴 mp3 — 이름은 그 시절 키
        /// md5("edge|en-GB-RyanNeural|+10%|-6Hz|텍스트")[:20].mp3. 절대 재생되면 안 된다.
        /// </summary>
        private static void WriteLegacyEdgeMp3(string dir, string text)
        {
            string name;
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("edge|en-GB-RyanNeural|+10%|-6Hz|" + text));
                name = string.Concat(hash.Select(b => b.ToString("x2"))).Substring(0, 20) + ".mp3";
            }
            File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 0x49, 0x44, 0x33, 1, 2, 3 });
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

        /// <summary>플러그인 CreateRadio와 같은 조립 (팩 상태로 경로를 막거나 연다).</summary>
        private static RadioVoice Radio(string dir, FakePlayer player)
        {
            string packState;
            return new RadioVoice(VoicePolicy.CreateRoute(dir, out packState), player);
        }

        /// <summary>팩 상태와 무관하게 폴더의 클립을 찾는 경로 — 픽스처가 비어 있지 않은지 확인용.</summary>
        private static VoiceSource Unblocked(string dir, string text)
        {
            return new VoiceRoute(new VoiceCache(dir)).Plan(text, "casual").Source;
        }

        private static string PackStateOf(string dir)
        {
            return VoicePolicy.KokoroPackState(dir, new VoiceCache(dir));
        }

        // -- 케이스 ------------------------------------------------------------

        private static void KokoroOnly()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, player);

            Check(PackStateOf(dir) == VoicePolicy.PackOk, "픽스처(자산 4개 + 확인 문장) → ok");
            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Played
                  && radio.LastSource == "KokoroFx", "A → KokoroFx 재생");
            Check(player.Files.Count == 1 && player.Files[0].EndsWith("_rfx3.wav"), "A는 _rfx3 파일");
            Check(radio.Speak(TextB, "casual", false) == SpeakResult.Played
                  && radio.LastSource == "KokoroSeg" && player.WavCalls == 1, "B → 세그먼트 조립 (PlayWav 1회)");
            Check(radio.Speak(TextC, "casual", false) == SpeakResult.Played
                  && radio.LastSource == "KokoroRaw", "C → 원본 통문장");
            int before = player.Calls;
            Check(radio.Speak(TextD, "casual", false) == SpeakResult.Dropped
                  && radio.LastReason == VoiceRoute.ReasonMiss, "D(옛 edge mp3만) → 무음 kokoro-miss");
            Check(radio.Speak(TextE, "urgent", true) == SpeakResult.Dropped, "E → 무음");
            Check(radio.Speak(TextE, "urgent", true) == SpeakResult.Dropped, "E 다시 → 무음");
            Check(player.Calls == before, "무음 콜은 재생기에 안 간다");
            Check(!player.Files.Any(f => !f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)),
                  "wav 말고는 재생하지 않는다 (옛 mp3 포함)");
            Check(radio.Drops.Count == 3 && radio.Drops.Distinct == 2, "무음 3건, 서로 다른 텍스트 2종");

            var failing = new FakePlayer { Succeed = false };
            RadioVoice r2 = Radio(dir, failing);
            Check(r2.Speak(TextA, "casual", false) == SpeakResult.Dropped && r2.LastReason == "play-failed",
                  "재생 실패 → 무음 play-failed");
            Check(failing.Calls == 1, "재생 실패를 다른 소스로 다시 시도하지 않는다");
        }

        private static void NoPack()
        {
            // 폴더 없음 — 플러그인이 처음 설치됐을 때 (DLL 옆 audio_cache가 아직 없다)
            string missing = Path.Combine(_root, "nope");
            var p0 = new FakePlayer();
            RadioVoice r0 = Radio(missing, p0);
            Check(PackStateOf(missing) == VoicePolicy.PackNone
                  && VoicePolicy.PackNote(VoicePolicy.PackNone) == VoicePolicy.NoteNoKokoro,
                  "폴더 없음 → none (no-kokoro)");
            foreach (string t in new[] { TextA, TextB, TextC, TextE })
                Check(r0.Speak(t, "casual", false) == SpeakResult.Dropped && r0.LastReason == VoiceRoute.ReasonNoPack,
                      "폴더 없음: " + t + " → 무음 no-pack");
            Check(p0.Calls == 0 && r0.Drops.Count == 4, "폴더 없음: 재생기 0회, 무음 4건");

            // 빈 폴더 — 팩을 지웠거나 아직 받지 않음
            string empty = NewDir();
            var p1 = new FakePlayer();
            RadioVoice r1 = Radio(empty, p1);
            Check(PackStateOf(empty) == VoicePolicy.PackNone, "빈 폴더 → none");
            Check(r1.Speak(TextA, "casual", false) == SpeakResult.Dropped && r1.LastReason == VoiceRoute.ReasonNoPack
                  && p1.Calls == 0, "빈 폴더: 무음 no-pack, 재생기 0회");

            // 옛 edge mp3만 남은 폴더 (예전 버전이 edge로 말하던 설치) — 팩이 아니다
            string legacy = NewDir();
            WriteLegacyEdgeMp3(legacy, TextA);
            var p2 = new FakePlayer();
            RadioVoice r2 = Radio(legacy, p2);
            Check(PackStateOf(legacy) == VoicePolicy.PackNone, "옛 edge mp3만 → none");
            Check(r2.Speak(TextA, "casual", false) == SpeakResult.Dropped && p2.Calls == 0,
                  "옛 edge mp3만: 같은 문장도 무음, mp3를 재생하지 않는다");

            // 설치가 끊긴 표식 + 남은 클립 없음 → incomplete, 무음 세션
            string broken = NewDir();
            AudioPack.WriteMarker(broken, AudioPack.IncompletePrefix + "kokoro-v016");
            var p3 = new FakePlayer();
            RadioVoice r3 = Radio(broken, p3);
            string st3 = PackStateOf(broken);
            Check(st3 == VoicePolicy.PackIncomplete && !VoicePolicy.PackUsable(st3)
                  && VoicePolicy.PackNote(st3) == VoicePolicy.NotePackIncomplete,
                  "설치 끊김 + 클립 없음 → incomplete (pack-incomplete), 사용 불가");
            Check(r3.Speak(TextE, "urgent", true) == SpeakResult.Dropped && p3.Calls == 0 && r3.Drops.Count == 1,
                  "설치 끊김: 긴급 콜도 무음, 재생기 0회");

            // 설치가 끊긴 표식 + 확인 문장은 없지만 다른 클립이 남음 (풀기 도중 중단·디스크 가득)
            // → incomplete. 남은 클립도 재생하지 않는다 — 설정 화면·로그의 '무음' 안내와 같게
            string cut = NewDir();
            AudioPack.WriteMarker(cut, AudioPack.IncompletePrefix + "kokoro-v016");
            WriteLeftovers(cut, true);
            Check(Unblocked(cut, TextF) == VoiceSource.KokoroFx && Unblocked(cut, TextB) == VoiceSource.KokoroSeg
                  && Unblocked(cut, TextC) == VoiceSource.KokoroRaw, "설치 끊김 픽스처: 남은 클립이 실제로 있다");
            var p4 = new FakePlayer();
            RadioVoice r4 = Radio(cut, p4);
            Check(PackStateOf(cut) == VoicePolicy.PackIncomplete, "설치 끊김 + 확인 문장 없음 + 다른 클립 남음 → incomplete");
            foreach (string t in new[] { TextF, TextB, TextC })
                Check(r4.Speak(t, "casual", false) == SpeakResult.Dropped && r4.LastReason == VoiceRoute.ReasonNoPack,
                      "설치 끊김: 남은 클립 " + t + " → 무음 no-pack");
            Check(p4.Calls == 0 && r4.Drops.Count == 3, "설치 끊김: 남은 클립이 있어도 재생기 0회");

            // 표식 없음 + 확인 문장 없음 + 다른 kokoro 클립 남음 (팩이 아닌 폴더) → none, 무음 세션
            string stray = NewDir();
            WriteLeftovers(stray, false);
            Check(Unblocked(stray, TextF) == VoiceSource.KokoroFx && Unblocked(stray, TextC) == VoiceSource.KokoroRaw,
                  "팩 아닌 폴더 픽스처: 남은 클립이 실제로 있다");
            var p5 = new FakePlayer();
            RadioVoice r5 = Radio(stray, p5);
            Check(PackStateOf(stray) == VoicePolicy.PackNone, "표식·확인 문장 없음 + 다른 클립 남음 → none");
            foreach (string t in new[] { TextF, TextC })
                Check(r5.Speak(t, "casual", false) == SpeakResult.Dropped && r5.LastReason == VoiceRoute.ReasonNoPack,
                      "팩 아닌 폴더: 남은 클립 " + t + " → 무음 no-pack");
            Check(p5.Calls == 0, "팩 아닌 폴더: 재생기 0회");
        }

        private static void PackTable()
        {
            Check(VoicePolicy.PackNote(VoicePolicy.PackOk) == null && VoicePolicy.PackUsable(VoicePolicy.PackOk),
                  "ok → 사유 없음, 사용");
            Check(VoicePolicy.PackNote(VoicePolicy.PackPartial) == VoicePolicy.NotePackPartial
                  && VoicePolicy.PackUsable(VoicePolicy.PackPartial), "partial → pack-partial, 사용 (남은 클립만)");
            Check(VoicePolicy.PackNote(VoicePolicy.PackIncomplete) == VoicePolicy.NotePackIncomplete
                  && !VoicePolicy.PackUsable(VoicePolicy.PackIncomplete), "incomplete → pack-incomplete, 무음 세션");
            Check(VoicePolicy.PackNote(VoicePolicy.PackNone) == VoicePolicy.NoteNoKokoro
                  && !VoicePolicy.PackUsable(VoicePolicy.PackNone), "none → no-kokoro, 무음 세션");
            Check(VoicePolicy.ProbeText() == TextA, "팩 확인 문장 = pit_call 첫 줄");

            string dir = NewFixture();
            Check(PackStateOf(dir) == VoicePolicy.PackOk, "자산 4개 → ok (옛 v015)");
            AudioPack.WriteMarker(dir, AudioPack.IncompletePrefix + "kokoro-v016");
            Check(PackStateOf(dir) == VoicePolicy.PackPartial, "설치 끊김 표식 + 확인용 통문장 남음 → partial");
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, player);
            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Played
                  && radio.Speak(TextE, "casual", false) == SpeakResult.Dropped,
                  "partial: 남은 클립은 재생, 없는 멘트는 무음");
            AudioPack.WriteMarker(dir, "kokoro-v016");
            Check(PackStateOf(dir) == VoicePolicy.PackOk, "완료 표식 → ok");
        }

        private static void Stops()
        {
            string dir = NewFixture();
            var player = new FakePlayer();
            RadioVoice radio = Radio(dir, player);
            radio.Stop();
            Check(radio.Speak(TextA, "casual", false) == SpeakResult.Stopped
                  && player.Calls == 0, "Stop 뒤 Speak → Stopped, 재생 0");

            var p2 = new FakePlayer();
            RadioVoice r2 = Radio(dir, p2);
            p2.StopDuringPlay = r2;
            Check(r2.Speak(TextA, "casual", false) == SpeakResult.Stopped && r2.Drops.Count == 0,
                  "재생 중 Stop(true) → Stopped, 무음 기록 아님");

            var p3 = new FakePlayer { Succeed = false };
            RadioVoice r3 = Radio(dir, p3);
            p3.StopDuringPlay = r3;
            Check(r3.Speak(TextA, "casual", false) == SpeakResult.Stopped && r3.Drops.Count == 0
                  && p3.Calls == 1, "Stop 뒤 재생 false → Stopped, 다시 시도 없음");
        }

        private static void Gate()
        {
            string dir = NewFixture();
            FakePlayer.Active = 0;
            FakePlayer.MaxActive = 0;
            var p1 = new FakePlayer { SleepMs = 15 };
            var p2 = new FakePlayer { SleepMs = 15 };
            RadioVoice r1 = Radio(dir, p1);
            RadioVoice r2 = Radio(dir, p2);
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
            RadioVoice radio = Radio(dir, player);
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
