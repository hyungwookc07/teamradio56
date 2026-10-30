using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Update;

namespace TeamRadio56.Core.Logic
{
    /// <summary>세션 목소리 — 엔진을 띄울 때 한 번 정하고 콜마다 다시 정하지 않는다.</summary>
    public enum VoiceKind { Kokoro, Edge }

    /// <summary>발화 결과 — Dropped는 세션 목소리로 낼 수 없어 자막만 남긴 콜.</summary>
    public enum SpeakResult { Played, Dropped, Stopped }

    /// <summary>
    /// 한 목소리 정책 — 한 엔진 실행 동안 모든 콜은 그 세션의 목소리로 나가거나
    /// 아예 나가지 않는다 (로그·최근 무전·자막에는 남는다). 다른 목소리로 대신
    /// 말하는 건 AllowVoiceFallback(기본 끔)을 켰을 때뿐.
    /// 파이썬 tts.kokoro_pack_state / resolve_voice_kind와 같은 규칙 (replay-check 대조).
    /// </summary>
    public static class VoicePolicy
    {
        public const string NoteKo = "ko";
        public const string NoteNoKokoro = "no-kokoro";
        public const string NotePackIncomplete = "pack-incomplete";
        public const string NotePackPartial = "pack-partial";

        public const string PackOk = "ok";
        public const string PackPartial = "partial";
        public const string PackIncomplete = "incomplete";
        public const string PackNone = "none";

        public static string KindName(VoiceKind kind)
        {
            return kind == VoiceKind.Kokoro ? "kokoro" : "edge";
        }

        /// <summary>
        /// 팩 상태 확인용 문장 — en 멘트 풀 pit_call/casual 첫 줄. 모든 팩(v015 포함)에
        /// 통문장으로 들어 있다. 멘트 언어와 무관하게 en 데이터에서 읽는다.
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
        /// 확인용 통문장이 남아 있으면 partial — 있는 클립은 kokoro로, 없는 멘트는 자막만.
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
        /// 세션 목소리 결정. ko는 kokoro가 없어 edge, edge는 edge,
        /// 그 밖(kokoro)은 팩이 있으면 kokoro, 설치가 끊겼어도 클립이 남아 있으면 kokoro(note
        /// pack-partial), 없으면 세션 전체를 edge로.
        /// runtime은 런타임 kokoro 합성 가능 여부 (C#은 항상 false).
        /// </summary>
        public static VoiceKind ResolveKind(string engine, bool ko, string packState,
                                            bool runtime, out string note)
        {
            note = null;
            bool edgeChosen = string.Equals(engine, "edge", StringComparison.OrdinalIgnoreCase);
            if (ko)
            {
                if (!edgeChosen)
                    note = NoteKo;
                return VoiceKind.Edge;
            }
            if (edgeChosen)
                return VoiceKind.Edge;
            if (packState == PackOk || runtime)
                return VoiceKind.Kokoro;
            if (packState == PackPartial)
            {
                note = NotePackPartial;
                return VoiceKind.Kokoro;
            }
            note = packState == PackIncomplete ? NotePackIncomplete : NoteNoKokoro;
            return VoiceKind.Edge;
        }

        /// <summary>RadioVoice.LastSource가 세션 목소리의 소스인가 ("sapi"·다른 목소리면 false).</summary>
        public static bool SourceIsKind(string source, VoiceKind kind)
        {
            if (string.IsNullOrEmpty(source))
                return false;
            return kind == VoiceKind.Kokoro
                ? source.StartsWith("Kokoro", StringComparison.Ordinal)
                : source.StartsWith("Edge", StringComparison.Ordinal);
        }

        /// <summary>Windows 음성(SAPI)은 설정을 켰을 때만, 한국어는 절대 아니다 (en-US 음성으로 읽는다).</summary>
        public static bool FallbackAllowed(bool setting, bool ko)
        {
            return setting && !ko;
        }

        /// <summary>
        /// 세션 목소리에 맞는 캐시 — kokoro 팩은 효과본(_rfx3)과 세그먼트만 담고 있어
        /// kokoro 세션은 무전 효과 설정과 무관하게 효과를 켠다. 효과 설정은 edge에만.
        /// </summary>
        public static VoiceCache CreateCache(string cacheDir, string edgeVoice, int ratePercent,
                                             VoiceKind kind, bool radioFxSetting,
                                             string kokoroVoice = "bm_george")
        {
            return new VoiceCache(cacheDir, edgeVoice, ratePercent, kokoroVoice,
                                  kind == VoiceKind.Kokoro || radioFxSetting, KindName(kind));
        }

        /// <summary>플러그인 CreateRadio와 같은 조립 — 셀프테스트가 같은 경로를 탄다.</summary>
        public static VoiceRoute CreateRoute(string cacheDir, string edgeVoice, int ratePercent,
                                             VoiceKind kind, bool radioFxSetting,
                                             bool allowFallback, EdgeSynthFn synth = null,
                                             Func<DateTime> utcNow = null)
        {
            VoiceCache cache = CreateCache(cacheDir, edgeVoice, ratePercent, kind, radioFxSetting);
            return new VoiceRoute(cache, kind, allowFallback, synth, utcNow);
        }

        /// <summary>
        /// 팩에 없는 멘트 수 — whole: 사전 캐시 대상 중 kokoro 통문장(효과본·원본)이 없는 것,
        /// seg: maxTier 이하 문장 중 세그먼트 파일이 없는 것 (maxTier 0이면 세지 않음).
        /// en 멘트 언어에서만 의미가 있다.
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
        public bool Record(string kind, string reason, string tone, string text, bool urgent)
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
            string line = "[무음] " + kind + " 목소리로 낼 수 없어 말하지 않음 (" + reason + ") ["
                          + (tone ?? "casual") + "] " + text;
            if (urgent)
                FileLog.Warn(line);
            else
                FileLog.Info(line);
            return true;
        }
    }
}
