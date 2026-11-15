using System;
using System.IO;
using System.Reflection;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 재생 장치 — wav를 끝까지 재생만 한다. 무엇을 낼지(kokoro 음성 팩: 효과 통문장 →
    /// 세그먼트 조립 → 원본 통문장, 없으면 무음)는 Core의 VoiceRoute, 게이트·무음 기록은
    /// RadioVoice가 맡는다. 재생 실패는 false로 알릴 뿐 다른 목소리로 대신 읽지 않는다.
    /// 음성 팩은 wav뿐이라 SoundPlayer 하나로 충분하다 (볼륨은 Windows 믹서로).
    /// </summary>
    public sealed class AudioSink : IRadioPlayer, IDisposable
    {
        private volatile bool _stopped;
        private System.Media.SoundPlayer _currentWav;

        /// <summary>
        /// 음성 팩 폴더 자동 탐색 — DLL 옆 audio_cache, 그다음 예전 설치의
        /// teamradio56-engine\audio_cache. 오디오가 실제로 든 폴더가 우선이고,
        /// 둘 다 비었으면 처음 있는 폴더, 아무것도 없으면 null.
        /// </summary>
        public static string FindCacheDir()
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
            // 오디오가 실제로 들어 있는 폴더 우선 — 빈 audio_cache가 진짜 팩을 가리면 안 된다
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
                using (var files = Directory.EnumerateFiles(dir, "*.wav").GetEnumerator())
                    return files.MoveNext();
            }
            catch (Exception) { }
            return false;
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

        /// <summary>wav를 끝까지 재생. 끝났거나 Stop()으로 끊겼으면 true, 실패하면 false.</summary>
        public bool PlayFile(string path)
        {
            if (_stopped)
                return true;
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                // 음성 팩은 wav뿐 — 다른 형식(옛 edge mp3 등)은 재생하지 않는다
                FileLog.Error("wav가 아닌 오디오는 재생하지 않음: " + path, null);
                return false;
            }
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
                FileLog.Error("음성 팩 오디오 재생 실패: " + path, ex);
                return false;
            }
            finally
            {
                _currentWav = null;
            }
        }

        /// <summary>
        /// 엔진 재시작/종료 — 재생 중인 wav를 즉시 끊는다. 이후 재생 요청은
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
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
