namespace TeamRadio56.Core.Logic
{
    /// <summary>재생 장치 — 플러그인 AudioSink가 구현. 모두 소리가 끝날 때까지 돌아오지 않는다.</summary>
    public interface IRadioPlayer
    {
        /// <summary>wav 파일을 끝까지 재생. 끝났거나 Stop()으로 끊겼으면 true.</summary>
        bool PlayFile(string path);

        /// <summary>조립된 wav 바이트를 끝까지 재생.</summary>
        bool PlayWav(byte[] wav);

        void Stop();
    }

    /// <summary>
    /// 한 목소리 발화 — VoiceRoute가 kokoro 음성 팩에서 찾은 소리를 재생하고, 없으면
    /// 말하지 않는다 (VoiceDropLog에 기록, 자막·최근 무전에는 남는다). 재생이 실패해도
    /// 다른 소스나 다른 목소리로 다시 시도하지 않는다.
    ///
    /// 겹침 방지 규칙: 이 프로세스의 모든 발화는 PlaybackGate 하나를 지나고,
    /// 출력 경로 모두 "소리가 끝날 때까지" 돌아오지 않는다. 그래서 엔진
    /// 재시작 중의 이전/새 싱크, [테스트 발화]까지 한 줄로 선다.
    /// </summary>
    public sealed class RadioVoice : IVoiceSink
    {
        // 인스턴스가 아니라 프로세스 단위 — 재시작으로 싱크가 바뀌어도 공유
        private static readonly object PlaybackGate = new object();

        private readonly VoiceRoute _route;
        private readonly IRadioPlayer _player;
        private volatile bool _stopped;

        public RadioVoice(VoiceRoute route, IRadioPlayer player)
        {
            _route = route;
            _player = player;
            Drops = new VoiceDropLog();
        }

        public VoiceRoute Route { get { return _route; } }
        public VoiceDropLog Drops { get; private set; }

        /// <summary>마지막으로 재생한 소스 (KokoroFx / KokoroSeg / KokoroRaw).</summary>
        public string LastSource { get; private set; }

        /// <summary>마지막으로 말하지 못한 사유 (no-pack / kokoro-miss / play-failed).</summary>
        public string LastReason { get; private set; }

        public SpeakResult Speak(string text, string tone, bool urgent)
        {
            if (_stopped || string.IsNullOrEmpty(text))
                return SpeakResult.Stopped;
            // 파일 확인·조립은 게이트 밖에서 — 앞 콜 재생 중에 준비해 둔다
            string reason;
            VoiceClip clip = _route.Resolve(text, tone, out reason);
            lock (PlaybackGate)
            {
                if (_stopped)
                    return SpeakResult.Stopped;   // 엔진 재시작 중 — 이전 싱크는 더 말하지 않는다
                if (clip != null)
                {
                    bool ok = clip.Wav != null ? _player.PlayWav(clip.Wav) : _player.PlayFile(clip.Path);
                    if (ok)
                    {
                        if (_stopped)
                            return SpeakResult.Stopped;
                        LastSource = clip.Source.ToString();
                        return SpeakResult.Played;
                    }
                    // Stop()으로 끊긴 재생은 무음 기록도 아니다
                    if (_stopped)
                        return SpeakResult.Stopped;
                    reason = "play-failed";
                }
                Drops.Record(reason ?? "?", tone, text, urgent);
                LastReason = reason ?? "?";
                return SpeakResult.Dropped;
            }
        }

        /// <summary>
        /// 엔진 재시작/종료 — 더 이상 말하지 않고, 재생 중인 wav는 즉시 끊는다.
        /// </summary>
        public void Stop()
        {
            _stopped = true;
            _player.Stop();
        }
    }
}
