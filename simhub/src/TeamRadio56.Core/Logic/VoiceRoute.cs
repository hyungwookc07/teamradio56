using System;
using System.Collections.Generic;
using System.Threading;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Logic
{
    public enum VoiceSource { None, KokoroFx, KokoroSeg, KokoroRaw, EdgeCache, EdgeLive }

    /// <summary>파일 확인만으로 정한 재생 계획 — 조립·합성은 아직 하지 않았다.</summary>
    public sealed class VoicePlan
    {
        public VoiceSource Source;
        public string Path;
        public string[] Segments;
        public string Reason;
        internal string Rate;
        internal string Pitch;
    }

    /// <summary>재생할 소리 — 파일 경로 또는 조립된 wav 바이트.</summary>
    public sealed class VoiceClip
    {
        public string Path;
        public byte[] Wav;
        public VoiceSource Source;
    }

    /// <summary>edge 합성 — 성공하면 target에 mp3를 쓰고 true.</summary>
    public delegate bool EdgeSynthFn(string text, string voice, string rate, string pitch,
                                     string target);

    /// <summary>
    /// 세션 목소리 하나로만 소리를 찾는다.
    ///   kokoro: 효과 통문장 → 문장 세그먼트 조립 → 원본 통문장 → (없으면 무음)
    ///   edge  : 캐시된 mp3 → 런타임 합성 → (오프라인이면 무음)
    /// kokoro 경로는 AllowFallback일 때만 edge 단계로 이어진다. edge 경로는 kokoro
    /// 파일을 보지 않는다. 파이썬 tts.VoiceChain과 같은 순서.
    /// </summary>
    public sealed class VoiceRoute
    {
        private const int SegMissLogLimit = 20;
        private const int SynthLogLimit = 10;
        private const int SynthFailLogLimit = 5;
        private const double SynthBlockSec = 120;

        private readonly VoiceCache _cache;
        private readonly EdgeSynthFn _synth;
        private readonly Func<DateTime> _utcNow;
        private long _blockedUntilTicks;
        private int _synthLogged;
        private int _synthFailLogged;
        // 세그먼트 미스는 같은 문장이 계속 반복되므로 (톤, 문장) 첫 20종만 기록
        private readonly HashSet<string> _segMissLogged = new HashSet<string>(StringComparer.Ordinal);

        public VoiceRoute(VoiceCache cache, VoiceKind kind, bool allowFallback,
                          EdgeSynthFn synth = null, Func<DateTime> utcNow = null)
        {
            _cache = cache;
            Kind = kind;
            AllowFallback = allowFallback;
            _synth = synth ?? ((t, v, r, p, o) => EdgeTtsClient.Synthesize(t, v, r, p, o));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public VoiceKind Kind { get; private set; }
        public bool AllowFallback { get; private set; }
        public VoiceCache Cache { get { return _cache; } }

        /// <summary>합성 실패 뒤 Windows 음성이 실제로 대신 말하는가 — RadioVoice가 정한다
        /// (섞임 허용이어도 한국어는 Windows 음성이 없다). 실패 로그 문구에만 쓴다.</summary>
        internal bool HasFallbackSpeaker { get; set; }

        /// <summary>재생 계획 (파일 확인만, 로그 없음).</summary>
        public VoicePlan Plan(string text, string tone)
        {
            string firstMissing;
            return Plan(text, tone, out firstMissing);
        }

        /// <summary>같은 계획 — 세그먼트 미스면 처음 빠진 문장도 (커버리지 퍼즈).</summary>
        public VoicePlan Plan(string text, string tone, out string firstMissing)
        {
            return PlanFrom(text, tone ?? "casual", false, out firstMissing);
        }

        /// <summary>계획을 실행해 재생할 소리를 만든다. 없으면 null과 사유.</summary>
        public VoiceClip Resolve(string text, string tone, out string reason)
        {
            string t = tone ?? "casual";
            string firstMissing;
            VoicePlan plan = PlanFrom(text, t, false, out firstMissing);
            if (firstMissing != null)
                LogSegMiss("코코로 문장 미스 [" + t + "] " + firstMissing);
            if (plan.Source == VoiceSource.KokoroSeg)
            {
                byte[] wav = RadioAssembler.Build(_cache.CacheDir, t, plan.Segments);
                if (wav != null)
                {
                    reason = null;
                    return new VoiceClip { Wav = wav, Source = VoiceSource.KokoroSeg };
                }
                LogSegMiss("코코로 세그먼트 조립 실패 [" + t + "] " + text);
                // 같은 목소리로 다음 단계 (원본 통문장)
                plan = PlanFrom(text, t, true, out firstMissing);
            }
            switch (plan.Source)
            {
                case VoiceSource.KokoroFx:
                case VoiceSource.KokoroRaw:
                case VoiceSource.EdgeCache:
                    reason = null;
                    return new VoiceClip { Path = plan.Path, Source = plan.Source };
                case VoiceSource.EdgeLive:
                    return SynthLive(text, t, plan, out reason);
                default:
                    reason = plan.Reason;
                    return null;
            }
        }

        private VoiceClip SynthLive(string text, string tone, VoicePlan plan, out string reason)
        {
            if (_synth(text, _cache.EdgeVoice, plan.Rate, plan.Pitch, plan.Path))
            {
                if (Interlocked.Increment(ref _synthLogged) <= SynthLogLimit)
                    FileLog.Info("캐시 미스 — edge 합성: [" + tone + "] " + text);
                reason = null;
                return new VoiceClip { Path = plan.Path, Source = VoiceSource.EdgeLive };
            }
            // 오프라인이면 콜마다 타임아웃으로 막히지 않게 잠시 끈다
            Interlocked.Exchange(ref _blockedUntilTicks,
                                 _utcNow().AddSeconds(SynthBlockSec).Ticks);
            if (Interlocked.Increment(ref _synthFailLogged) <= SynthFailLogLimit)
            {
                FileLog.Warn("edge 합성 실패(" + (EdgeTtsClient.LastError ?? "?") + ")"
                             + (HasFallbackSpeaker
                                 ? " — 2분간 Windows TTS로: "
                                 : " — 2분간 캐시에 있는 멘트만 (나머지는 자막만): ")
                             + text);
            }
            reason = "edge-failed";
            return null;
        }

        private VoicePlan PlanFrom(string text, string tone, bool afterAssembly,
                                   out string firstMissing)
        {
            firstMissing = null;
            if (_cache == null || !_cache.Available || string.IsNullOrEmpty(text))
                return new VoicePlan { Source = VoiceSource.None, Reason = "no-cache" };
            if (Kind == VoiceKind.Edge)
                return PlanEdge(text, tone, "edge-offline");

            if (!afterAssembly)
            {
                string fx = _cache.ResolveWholeFx(text, tone);
                if (fx != null)
                    return new VoicePlan { Source = VoiceSource.KokoroFx, Path = fx };
                string[] segs = _cache.ResolveSegments(text, tone, out firstMissing);
                if (segs != null)
                    return new VoicePlan { Source = VoiceSource.KokoroSeg, Segments = segs };
            }
            // 효과 전 원본 통문장은 조립 뒤 — 효과본 세그먼트를 가리면 안 된다
            string raw = _cache.ResolveWhole(text, tone);
            if (raw != null)
                return new VoicePlan { Source = VoiceSource.KokoroRaw, Path = raw };
            if (AllowFallback)
                return PlanEdge(text, tone, "kokoro-miss");
            return new VoicePlan { Source = VoiceSource.None, Reason = "kokoro-miss" };
        }

        private VoicePlan PlanEdge(string text, string tone, string offlineReason)
        {
            string cached = _cache.ResolveEdgeCached(text, tone);
            if (cached != null)
                return new VoicePlan { Source = VoiceSource.EdgeCache, Path = cached };
            string target, rate, pitch;
            if (_utcNow().Ticks >= Interlocked.Read(ref _blockedUntilTicks)
                && _cache.EdgeSynthTarget(text, tone, out target, out rate, out pitch))
            {
                return new VoicePlan
                {
                    Source = VoiceSource.EdgeLive, Path = target, Rate = rate, Pitch = pitch,
                };
            }
            return new VoicePlan { Source = VoiceSource.None, Reason = offlineReason };
        }

        private void LogSegMiss(string line)
        {
            lock (_segMissLogged)
            {
                if (_segMissLogged.Count >= SegMissLogLimit || !_segMissLogged.Add(line))
                    return;
            }
            FileLog.Info(line);
        }
    }
}
