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
        /// 오디오 캐시 폴더 자동 탐색 — DLL 옆, 엔진 폴더(teamradio56-engine),
        /// 엔진 실행 파일 옆, 그리고 추가 인자의 스크립트(main.py) 옆 순.
        /// 소스 모드에선 실행 파일이 venv의 python.exe라 ③으론 못 찾고,
        /// 추가 인자에 든 main.py 경로가 저장소(=audio_cache 위치)를 가리킨다.
        /// </summary>
        public static string FindCacheDir(string engineExePath, string engineArgs = null)
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
            try
            {
                string scriptDir = ScriptDirFromArgs(engineArgs);
                if (scriptDir != null)
                    candidates.Add(Path.Combine(scriptDir, "audio_cache"));
            }
            catch (Exception) { }
            foreach (string dir in candidates)
            {
                if (Directory.Exists(dir))
                    return dir;
            }
            return null;
        }

        /// <summary>추가 인자에서 첫 경로(보통 main.py)의 폴더를 뽑는다.</summary>
        private static string ScriptDirFromArgs(string args)
        {
            if (string.IsNullOrEmpty(args))
                return null;
            string path = args.Trim();
            if (path.StartsWith("\""))
            {
                int end = path.IndexOf('"', 1);
                if (end > 1)
                    path = path.Substring(1, end - 1);
            }
            else
            {
                int space = path.IndexOf(' ');
                if (space > 0)
                    path = path.Substring(0, space);
            }
            if (path.Length == 0)
                return null;
            return Path.GetDirectoryName(path);
        }

        private int _missLogged;

        public void Speak(string text, string tone, bool urgent)
        {
            string path = _cache != null ? _cache.Resolve(text, tone) : null;
            if (path == null && _missLogged < 10)
            {
                // 캐시 전멸(경로/규약 문제)인지 개별 미스인지 로그로 구분 가능하게
                _missLogged++;
                FileLog.Info("캐시 미스 — Windows TTS 폴백: [" + tone + "] " + text);
            }
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
