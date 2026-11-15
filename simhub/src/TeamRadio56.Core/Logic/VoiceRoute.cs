using System;
using System.Collections.Generic;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Logic
{
    public enum VoiceSource { None, KokoroFx, KokoroSeg, KokoroRaw }

    /// <summary>파일 확인만으로 정한 재생 계획 — 조립은 아직 하지 않았다.</summary>
    public sealed class VoicePlan
    {
        public VoiceSource Source;
        public string Path;
        public string[] Segments;
        public string Reason;
    }

    /// <summary>재생할 소리 — 파일 경로 또는 조립된 wav 바이트.</summary>
    public sealed class VoiceClip
    {
        public string Path;
        public byte[] Wav;
        public VoiceSource Source;
    }

    /// <summary>
    /// kokoro 음성 팩 하나로만 소리를 찾는다:
    ///   효과 통문장 → 문장 세그먼트 조립 → 원본 통문장 → (없으면 무음, 자막만)
    /// 다른 목소리로 대신 말하지 않는다. 파이썬 tts.VoiceChain(kokoro)과 같은 순서.
    /// 사유: 음성 팩 폴더가 없거나 쓸 수 없는 팩(none/incomplete — 남은 클립이 있어도)이면
    /// "no-pack", 팩에 없는 멘트는 "kokoro-miss".
    /// </summary>
    public sealed class VoiceRoute
    {
        public const string ReasonNoPack = "no-pack";
        public const string ReasonMiss = "kokoro-miss";

        private const int SegMissLogLimit = 20;

        private readonly VoiceCache _cache;
        private readonly bool _packUsable;
        // 세그먼트 미스는 같은 문장이 계속 반복되므로 (톤, 문장) 첫 20종만 기록
        private readonly HashSet<string> _segMissLogged = new HashSet<string>(StringComparer.Ordinal);

        public VoiceRoute(VoiceCache cache) : this(cache, true)
        {
        }

        /// <param name="packUsable">
        /// false면 폴더에 클립이 남아 있어도 모든 콜이 무음(no-pack) — 설치가 끊겼거나
        /// 팩이 아닌 폴더(VoicePolicy.PackUsable). 캐시는 경로·로그용으로 그대로 둔다.
        /// </param>
        public VoiceRoute(VoiceCache cache, bool packUsable)
        {
            _cache = cache;
            _packUsable = packUsable;
        }

        public VoiceCache Cache { get { return _cache; } }

        /// <summary>이 발화 경로가 팩 클립을 재생하는가 (false면 세션 전체 무음).</summary>
        public bool PackUsable { get { return _packUsable; } }

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
            if (plan.Source == VoiceSource.KokoroFx || plan.Source == VoiceSource.KokoroRaw)
            {
                reason = null;
                return new VoiceClip { Path = plan.Path, Source = plan.Source };
            }
            reason = plan.Reason;
            return null;
        }

        private VoicePlan PlanFrom(string text, string tone, bool afterAssembly,
                                   out string firstMissing)
        {
            firstMissing = null;
            if (!_packUsable || _cache == null || !_cache.Available || string.IsNullOrEmpty(text))
                return new VoicePlan { Source = VoiceSource.None, Reason = ReasonNoPack };

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
            return new VoicePlan { Source = VoiceSource.None, Reason = ReasonMiss };
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
