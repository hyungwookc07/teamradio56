using System;
using System.IO;
using System.Reflection;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 내장(C#) 엔진의 발화 출력 — 3단 폴백:
    ///   1) 사전 생성 오디오 캐시 (무전 효과 wav 우선, mp3도 재생)
    ///   2) 캐시 미스 → edge 서비스로 런타임 합성 (mp3, 캐시에 저장돼 재사용)
    ///   3) 오프라인/실패 → Windows TTS(SpeechOutput)
    /// 파이썬 엔진의 ChainEngine[캐시 → edge] + SAPI 폴백과 같은 구조.
    /// 런타임 합성분에는 아직 무전 효과가 안 입혀진다 (캐시 항목만).
    ///
    /// 겹침 방지 규칙: 이 프로세스의 모든 발화는 PlaybackGate 하나를 지나고,
    /// 세 출력 경로 모두 "소리가 끝날 때까지" 돌아오지 않는다. 그래서 엔진
    /// 전환 중의 이전/새 싱크, [테스트 발화]까지 한 줄로 선다.
    /// </summary>
    public sealed class AudioSink : IVoiceSink, IDisposable
    {
        // 인스턴스가 아니라 프로세스 단위 — 재시작으로 싱크가 바뀌어도 공유
        private static readonly object PlaybackGate = new object();

        private readonly VoiceCache _cache;
        private readonly SpeechOutput _fallback;
        private readonly Mp3Player _mp3 = new Mp3Player();
        private readonly double _volume;
        private int _synthFailLogged;
        private DateTime _synthBlockedUntil = DateTime.MinValue;
        private volatile bool _stopped;
        private System.Media.SoundPlayer _currentWav;

        public AudioSink(VoiceCache cache, SpeechOutput fallback, double volume = 0.9)
        {
            _cache = cache;
            _fallback = fallback;
            _volume = volume;
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
            // 오디오가 실제로 들어 있는 폴더 우선 — edge 합성용으로 자동 생성된
            // 빈 audio_cache가 진짜 배포 캐시를 가리면 안 된다
            string firstExisting = null;
            foreach (string dir in candidates)
            {
                if (!Directory.Exists(dir))
                    continue;
                if (HasAudio(dir))
                    return dir;
                if (firstExisting == null)
                    firstExisting = dir;
            }
            return firstExisting;
        }

        private static bool HasAudio(string dir)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    if (f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch (Exception) { }
            return false;
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
            if (_stopped || string.IsNullOrEmpty(text))
                return;
            string path = ResolveOrSynthesize(text, tone);
            lock (PlaybackGate)
            {
                if (_stopped)
                    return;   // 엔진 전환 중 — 이전 싱크는 더 말하지 않는다
                if (path != null && PlayFile(path))
                    return;
                // Stop()으로 끊긴 재생을 TTS로 다시 읽지 않는다
                if (_stopped)
                    return;
                if (_fallback != null)
                    _fallback.SayAndWait(text);   // 동기 — 다음 콜과 겹치지 않게
            }
        }

        /// <summary>캐시 → (미스면) edge 합성. 둘 다 없으면 null.</summary>
        private string ResolveOrSynthesize(string text, string tone)
        {
            string path = _cache != null ? _cache.Resolve(text, tone) : null;

            if (path == null && _cache != null && DateTime.UtcNow >= _synthBlockedUntil)
            {
                // 캐시 미스 — edge로 런타임 합성해 캐시에 저장 (파이썬과 같은 키)
                string target, rate, pitch;
                if (_cache.EdgeSynthTarget(text, tone, out target, out rate, out pitch))
                {
                    if (EdgeTtsClient.Synthesize(text, _cache.EdgeVoice, rate, pitch, target))
                    {
                        path = target;
                        if (_missLogged < 10)
                        {
                            _missLogged++;
                            FileLog.Info("캐시 미스 — edge 합성: [" + tone + "] " + text);
                        }
                    }
                    else
                    {
                        // 오프라인이면 콜마다 타임아웃으로 막히지 않게 잠시 끈다
                        _synthBlockedUntil = DateTime.UtcNow.AddSeconds(120);
                        if (_synthFailLogged < 5)
                        {
                            _synthFailLogged++;
                            FileLog.Warn("edge 합성 실패(" + (EdgeTtsClient.LastError ?? "?")
                                         + ") — 2분간 Windows TTS로: " + text);
                        }
                    }
                }
            }
            return path;
        }

        /// <summary>wav/mp3를 끝까지 재생. 실패하면 false (호출부가 TTS 폴백).</summary>
        private bool PlayFile(string path)
        {
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var player = new System.Media.SoundPlayer(path))
                    {
                        _currentWav = player;
                        // Stop()이 _currentWav를 보기 직전에 끼어든 경우
                        if (_stopped)
                            return true;
                        player.PlaySync();   // 전용 스레드라 블로킹해도 된다
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    FileLog.Error("캐시 오디오 재생 실패 — TTS 폴백: " + path, ex);
                    return false;
                }
                finally
                {
                    _currentWav = null;
                }
            }
            if (_mp3.PlaySync(path, _volume))
                return true;
            FileLog.Error("mp3 재생 실패 — TTS 폴백: " + path, null);
            return false;
        }

        /// <summary>
        /// 엔진 전환/종료 — 더 이상 말하지 않고, 재생 중인 wav/mp3는 즉시
        /// 끊는다. (진행 중인 Windows TTS 문장은 끝까지 간다 — 게이트가
        /// 다음 발화를 그 뒤로 세운다.)
        /// </summary>
        public void Stop()
        {
            _stopped = true;
            try
            {
                System.Media.SoundPlayer wav = _currentWav;
                if (wav != null)
                    wav.Stop();
            }
            catch (Exception) { }
            _mp3.StopCurrent();
        }

        public void Dispose()
        {
            Stop();
            _mp3.Dispose();
        }
    }
}
