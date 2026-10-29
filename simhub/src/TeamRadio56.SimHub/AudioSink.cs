using System;
using System.IO;
using System.Reflection;
using TeamRadio56.Core.Diagnostics;
using TeamRadio56.Core.Logic;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 내장(C#) 엔진의 발화 출력 — 폴백 순서:
    ///   1) kokoro 통문장 캐시 (무전 효과가 켜져 있으면 효과 wav만)
    ///   2) 문장 세그먼트 조립 (모든 문장이 있을 때만, 메모리 PCM16 wav)
    ///   3) 효과 전 kokoro 통문장 원본 (효과 켜짐인데 _rfx3가 없을 때)
    ///   4) 이전에 저장된 edge mp3 캐시
    ///   5) 캐시 미스 → edge 서비스로 런타임 합성 (mp3, 캐시에 저장돼 재사용)
    ///   6) 오프라인/실패 → Windows TTS(SpeechOutput)
    /// 파이썬 엔진의 ChainEngine[효과 통문장 → 세그먼트 → 원본 통문장 → edge] + SAPI
    /// 폴백과 같은 구조 (파이썬은 3)에 효과를 입히고, C#은 원본 그대로 낸다).
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
        // 세그먼트 미스는 같은 문장이 계속 반복되므로 (톤, 문장) 첫 20종만 기록
        private const int SegMissLogLimit = 20;
        private readonly System.Collections.Generic.HashSet<string> _segMissLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private volatile bool _memPlayBroken;
        private bool _tempPlayFailLogged;

        public void Speak(string text, string tone, bool urgent)
        {
            if (_stopped || string.IsNullOrEmpty(text))
                return;
            // 파일 확인·조립·합성은 게이트 밖에서 — 앞 콜 재생 중에 준비해 둔다
            string path = _cache != null ? _cache.ResolveWholeFx(text, tone) : null;
            byte[] wav = null;
            if (path == null)
                wav = TryAssemble(text, tone);
            // 효과 전 원본 통문장은 조립 뒤 — 효과본 세그먼트를 가리면 안 된다
            if (path == null && wav == null && _cache != null)
                path = _cache.ResolveWhole(text, tone);
            if (path == null && wav == null)
                path = ResolveEdgeOrSynthesize(text, tone);
            lock (PlaybackGate)
            {
                if (_stopped)
                    return;   // 엔진 전환 중 — 이전 싱크는 더 말하지 않는다
                if (wav != null && PlayWavBytes(wav))
                    return;
                if (path != null && PlayFile(path))
                    return;
                // Stop()으로 끊긴 재생을 TTS로 다시 읽지 않는다
                if (_stopped)
                    return;
                if (_fallback != null)
                    _fallback.SayAndWait(text);   // 동기 — 다음 콜과 겹치지 않게
            }
        }

        /// <summary>
        /// 문장 세그먼트 조립 — 모든 문장이 캐시에 있을 때만 PCM16 wav 바이트.
        /// 하나라도 없거나 조립이 실패하면 null (부분 조립은 하지 않는다).
        /// </summary>
        private byte[] TryAssemble(string text, string tone)
        {
            if (_cache == null)
                return null;
            string t = tone ?? "casual";
            string firstMissing;
            string[] segs;
            try
            {
                segs = _cache.ResolveSegments(text, t, out firstMissing);
            }
            catch (Exception ex)
            {
                FileLog.Error("문장 세그먼트 확인 실패: " + text, ex);
                return null;
            }
            if (segs == null)
            {
                if (firstMissing != null)
                    LogSegMiss("코코로 문장 미스 [" + t + "] " + firstMissing);
                return null;
            }
            byte[] wav = RadioAssembler.Build(_cache.CacheDir, t, segs);
            if (wav == null)
                LogSegMiss("코코로 세그먼트 조립 실패 [" + t + "] " + text);
            return wav;
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

        /// <summary>
        /// 조립된 wav 바이트를 끝까지 재생. 메모리 재생이 안 되는 환경이면 한 번
        /// 기록하고 임시 파일(%TEMP%\teamradio56\assembled.wav, 게이트가 직렬화)로
        /// 기존 PlayFile 경로를 탄다. 실패하면 false (호출부가 TTS 폴백).
        /// </summary>
        private bool PlayWavBytes(byte[] wav)
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
                    FileLog.Error("조립 오디오 임시 파일 쓰기 실패 — TTS 폴백", ex);
                }
                return false;
            }
            return PlayFile(tmp);
        }

        /// <summary>캐시된 edge mp3 → (없으면) edge 합성. 둘 다 없으면 null.</summary>
        private string ResolveEdgeOrSynthesize(string text, string tone)
        {
            string path = _cache != null ? _cache.ResolveEdgeCached(text, tone) : null;

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
