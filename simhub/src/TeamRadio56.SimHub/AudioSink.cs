using System;
using System.IO;
using System.Reflection;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 재생 장치 — 소리를 끝까지 재생만 한다. 무엇을 낼지(세션 목소리 하나:
    /// kokoro 효과 통문장 → 세그먼트 조립 → 원본 통문장, 또는 edge 캐시 → 합성,
    /// 없으면 무음)는 Core의 VoiceRoute, 게이트·무음 기록은 RadioVoice가 맡는다.
    /// 재생 실패는 false로 알릴 뿐 다른 목소리로 대신 읽지 않는다.
    /// </summary>
    public sealed class AudioSink : IRadioPlayer, IDisposable
    {
        private readonly Mp3Player _mp3 = new Mp3Player();
        private readonly double _volume;
        private volatile bool _stopped;
        private System.Media.SoundPlayer _currentWav;

        public AudioSink(double volume = 0.9)
        {
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

        private volatile bool _memPlayBroken;
        private bool _tempPlayFailLogged;

        /// <summary>
        /// 조립된 wav 바이트를 끝까지 재생. 메모리 재생이 안 되는 환경이면 한 번
        /// 기록하고 임시 파일(%TEMP%\teamradio56\assembled.wav, 게이트가 직렬화)로
        /// 기존 PlayFile 경로를 탄다. 실패하면 false.
        /// </summary>
        public bool PlayWav(byte[] wav)
        {
            if (!_memPlayBroken)
            {
                try
                {
                    using (var ms = new MemoryStream(wav, false))
                    using (var player = new System.Media.SoundPlayer(ms))
                    {
                        _currentWav = player;
                        // Stop()이 _currentWav를 보기 직전에 끼어든 경우
                        if (_stopped)
                            return true;
                        player.PlaySync();
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    if (_stopped)
                        return true;   // Stop()이 끊으며 난 예외 — 메모리 재생 고장이 아니다
                    _memPlayBroken = true;
                    FileLog.Error("조립 오디오 메모리 재생 실패 — 이후 임시 파일로 재생", ex);
                }
                finally
                {
                    _currentWav = null;
                }
            }
            // 끊긴 재생을 임시 파일로 다시 틀지 않는다
            if (_stopped)
                return true;
            string tmp;
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "teamradio56");
                Directory.CreateDirectory(dir);
                tmp = Path.Combine(dir, "assembled.wav");
                File.WriteAllBytes(tmp, wav);
            }
            catch (Exception ex)
            {
                if (!_tempPlayFailLogged)
                {
                    _tempPlayFailLogged = true;
                    FileLog.Error("조립 오디오 임시 파일 쓰기 실패", ex);
                }
                return false;
            }
            return PlayFile(tmp);
        }

        /// <summary>wav/mp3를 끝까지 재생. 끝났거나 Stop()으로 끊겼으면 true, 실패하면 false.</summary>
        public bool PlayFile(string path)
        {
            if (_stopped)
                return true;
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
                    FileLog.Error("캐시 오디오 재생 실패: " + path, ex);
                    return false;
                }
                finally
                {
                    _currentWav = null;
                }
            }
            if (_mp3.PlaySync(path, _volume))
                return true;
            FileLog.Error("mp3 재생 실패: " + path, null);
            return false;
        }

        /// <summary>
        /// 엔진 전환/종료 — 재생 중인 wav/mp3를 즉시 끊는다. 이후 재생 요청은
        /// 소리 없이 true (끊긴 것으로 취급).
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
