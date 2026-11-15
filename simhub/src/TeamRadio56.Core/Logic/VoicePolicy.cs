using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Update;

namespace TeamRadio56.Core.Logic
{
    /// <summary>발화 결과 — Dropped는 음성 팩에 없어(또는 팩이 없어) 자막만 남긴 콜.</summary>
    public enum SpeakResult { Played, Dropped, Stopped }

    /// <summary>
    /// 한 목소리 정책 — 플러그인의 무전 목소리는 kokoro 음성 팩 하나뿐이다. 모든 콜은
    /// 팩의 클립으로 나가거나 아예 나가지 않는다 (로그·최근 무전·자막에는 남는다).
    /// 다른 목소리(온라인 TTS·Windows 음성)로 대신 말하는 길은 없다.
    /// 팩 상태 판정은 파이썬 tts.kokoro_pack_state와 같은 규칙 (replay-check 대조).
    /// 파이썬 단독 앱은 팩이 없으면 edge로 말하지만, 플러그인은 그 자리가 무음이다.
    /// </summary>
    public static class VoicePolicy
    {
        public const string NoteNoKokoro = "no-kokoro";
        public const string NotePackIncomplete = "pack-incomplete";
        public const string NotePackPartial = "pack-partial";

        public const string PackOk = "ok";
        public const string PackPartial = "partial";
        public const string PackIncomplete = "incomplete";
        public const string PackNone = "none";

        /// <summary>
        /// 팩 상태 확인용 문장 — en 멘트 풀 pit_call/casual 첫 줄. 모든 팩(v015 포함)에
        /// 통문장으로 들어 있다.
        /// </summary>
        public static string ProbeText()
        {
            string fallback = null;
            foreach (PhraseSet set in PhraseLines.All)
            {
                if (set.Pool != "pit_call" || set.Lines == null || set.Lines.Length == 0)
                    continue;
                if (set.Tone == "casual")
                    return set.Lines[0];
                if (fallback == null)
                    fallback = set.Lines[0];
            }
            return fallback;
        }

        /// <summary>
        /// kokoro 음성 팩 상태 — "ok" | "partial" | "incomplete" | "none".
        /// 표식이 있으면 표식대로, 없으면 조립 자산 4개(옛 v015) 또는 확인용 통문장
        /// (_rfx3나 원본, 개발 캐시)이 있으면 ok. 설치가 끊긴 표식(incomplete:)이어도
        /// 확인용 통문장이 남아 있으면 partial — 있는 클립은 말하고, 없는 멘트는 자막만.
        /// probe는 kokoro 캐시 규약의 VoiceCache.
        /// </summary>
        public static string KokoroPackState(string cacheDir, VoiceCache probe)
        {
            if (string.IsNullOrEmpty(cacheDir) || !Directory.Exists(cacheDir))
                return PackNone;
            string id = AudioPack.Installed(cacheDir);
            if (id != null)
            {
                if (!id.StartsWith(AudioPack.IncompletePrefix, StringComparison.Ordinal))
                    return PackOk;
                return ProbeClipExists(probe) ? PackPartial : PackIncomplete;
            }
            return ProbeClipExists(probe) ? PackOk : PackNone;
        }

        /// <summary>확인용 통문장이 캐시에 있는가 (_rfx3 또는 원본).</summary>
        private static bool ProbeClipExists(VoiceCache probe)
        {
            if (probe == null)
                return false;
            string text = ProbeText();
            return text != null && probe.ResolveWhole(text, "casual") != null;
        }

        /// <summary>
        /// 팩 상태 → 설정 화면·로그용 사유 (ok면 null). 사유 문자열은 파이썬
        /// resolve_voice_kind의 note와 같다 (replay-check 결정표 대조).
        /// </summary>
        public static string PackNote(string packState)
        {
            switch (packState)
            {
                case PackOk: return null;
                case PackPartial: return NotePackPartial;
                case PackIncomplete: return NotePackIncomplete;
                default: return NoteNoKokoro;
            }
        }

        /// <summary>
        /// 이 팩으로 말할 수 있는가 — ok, 또는 설치가 끊겼어도 클립이 남은 partial.
        /// 아니면(none/incomplete) 이번 세션은 전부 무음(자막만)이다 — 폴더에 클립이
        /// 남아 있어도 재생하지 않는다 (RouteFor가 경로를 막는다).
        /// </summary>
        public static bool PackUsable(string packState)
        {
            return packState == PackOk || packState == PackPartial;
        }

        /// <summary>
        /// 팩 상태에 맞는 발화 경로 — 쓸 수 없는 팩(none/incomplete)이면 남은 클립이
        /// 있어도 모든 콜이 무음(no-pack). 파이썬이 그 자리에서 kokoro를 쓰지 않는 것과 같다.
        /// </summary>
        public static VoiceRoute RouteFor(VoiceCache cache, string packState)
        {
            return new VoiceRoute(cache, PackUsable(packState));
        }

        /// <summary>
        /// 플러그인 CreateRadio와 같은 조립 — 팩 상태를 정하고 그 상태로 경로를 막거나 연다.
        /// 셀프테스트가 같은 경로를 탄다.
        /// </summary>
        public static VoiceRoute CreateRoute(string cacheDir, out string packState)
        {
            var cache = new VoiceCache(cacheDir);
            packState = KokoroPackState(cacheDir, cache);
            return RouteFor(cache, packState);
        }

        /// <summary>
        /// 팩에 없는 멘트 수 — whole: 사전 캐시 대상 중 kokoro 통문장(효과본·원본)이 없는 것,
        /// seg: maxTier 이하 문장 중 세그먼트 파일이 없는 것 (maxTier 0이면 세지 않음).
        /// </summary>
        public static void CountMissing(VoiceCache cache, int maxTier, IEnumerable<string> tracks,
                                        out int whole, out int seg)
        {
            whole = 0;
            seg = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> item in PregenTexts.Enumerate(new PhrasePool()))
            {
                if (!seen.Add(item.Key + "\t" + item.Value))
                    continue;
                if (cache.ResolveWholeFx(item.Value, item.Key) == null
                    && cache.ResolveWhole(item.Value, item.Key) == null)
                    whole++;
            }
            if (maxTier <= 0 || !cache.Available)
                return;
            foreach (KeyValuePair<string, string> item in PregenSentences.Enumerate(maxTier, tracks))
            {
                if (!File.Exists(Path.Combine(cache.CacheDir,
                        VoiceCache.SegmentFileName(item.Value, item.Key))))
                    seg++;
            }
        }
    }

    /// <summary>
    /// 말하지 않은 콜 기록 — 건수는 모두 세고, 로그는 텍스트별 처음 한 번만
    /// (첫 50종까지). 같은 콜이 레이스 내내 반복되므로 로그가 넘치지 않게.
    /// </summary>
    public sealed class VoiceDropLog
    {
        public const int Limit = 50;

        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private int _count;
        private bool _capLogged;

        public int Count
        {
            get { return Volatile.Read(ref _count); }
        }

        public int Distinct
        {
            get
            {
                lock (_seen)
                    return _seen.Count;
            }
        }

        /// <summary>기록하고, 이번에 로그를 남겼으면 true.</summary>
        public bool Record(string reason, string tone, string text, bool urgent)
        {
            Interlocked.Increment(ref _count);
            bool capNow = false;
            lock (_seen)
            {
                if (_seen.Contains(text ?? ""))
                    return false;
                if (_seen.Count >= Limit)
                {
                    if (_capLogged)
                        return false;
                    _capLogged = true;
                    capNow = true;
                }
                else
                {
                    _seen.Add(text ?? "");
                }
            }
            if (capNow)
            {
                FileLog.Info("[무음] 기록 상한 " + Limit + "종 — 이후는 건수만 셉니다");
                return false;
            }
            // 파이썬 tts 무음 로그와 같은 형식 (목소리는 항상 kokoro)
            string line = "[무음] kokoro 목소리로 낼 수 없어 말하지 않음 (" + reason + ") ["
                          + (tone ?? "casual") + "] " + text;
            if (urgent)
                FileLog.Warn(line);
            else
                FileLog.Info(line);
            return true;
        }
    }
}
