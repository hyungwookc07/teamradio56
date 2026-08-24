using System;
using System.IO;
using System.Reflection;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 내장(C#) 엔진의 발화 출력 — 사전 생성 오디오 캐시(무전 효과 wav)를
    /// 우선 재생하고, 캐시에 없으면 Windows TTS(SpeechOutput)로 폴백한다.
    ///
    /// SoundPlayer는 wav만 지원하지만, 무전 효과가 켜진 캐시는 전부 wav라
    /// 충분하다 (mp3 원본만 있는 항목은 TTS 폴백).
    /// </summary>
    public sealed class AudioSink : IVoiceSink, IDisposable
    {
        private readonly VoiceCache _cache;
        private readonly SpeechOutput _fallback;

        public AudioSink(VoiceCache cache, SpeechOutput fallback)
        {
            _cache = cache;
            _fallback = fallback;
        }

        /// <summary>
        /// 오디오 캐시 폴더 자동 탐색 — DLL 옆 audio_cache,
        /// 엔진 폴더(teamradio56-engine)\audio_cache 순.
        /// </summary>
        public static string FindCacheDir(string engineExePath)
        {
            var candidates = new System.Collections.Generic.List<string>();
            try
            {
                string dllDir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (dllDir != null)
                {
                    candidates.Add(Path.Combine(dllDir, "audio_cache"));
                    candidates.Add(Path.Combine(dllDir, "teamradio56-engine", "audio_cache"));
                }
            }
            catch (Exception) { }
            try
            {
                if (!string.IsNullOrEmpty(engineExePath))
                {
                    string exeDir = Path.GetDirectoryName(engineExePath);
                    if (exeDir != null)
                        candidates.Add(Path.Combine(exeDir, "audio_cache"));
                }
            }
            catch (Exception) { }
            foreach (string dir in candidates)
            {
                if (Directory.Exists(dir))
                    return dir;
            }
            return null;
        }

        public void Speak(string text, string tone, bool urgent)
        {
            string path = _cache != null ? _cache.Resolve(text, tone) : null;
            if (path != null && path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var player = new System.Media.SoundPlayer(path))
                    {
                        player.PlaySync();   // 전용 스레드라 블로킹해도 된다
                    }
                    return;
                }
                catch (Exception ex)
                {
                    FileLog.Error("캐시 오디오 재생 실패 — TTS 폴백: " + path, ex);
                }
            }
            if (_fallback != null)
                _fallback.Say(text);
        }

        public void Dispose()
        {
        }
    }
}
