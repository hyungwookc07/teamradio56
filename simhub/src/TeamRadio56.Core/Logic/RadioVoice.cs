using System;

namespace TeamRadio56.Core.Logic
{
    /// <summary>재생 장치 — 플러그인 AudioSink가 구현. 모두 소리가 끝날 때까지 돌아오지 않는다.</summary>
    public interface IRadioPlayer
    {
        /// <summary>wav/mp3를 끝까지 재생. 끝났거나 Stop()으로 끊겼으면 true.</summary>
        bool PlayFile(string path);

        /// <summary>조립된 wav 바이트를 끝까지 재생.</summary>
        bool PlayWav(byte[] wav);

        void Stop();
    }

    /// <summary>다른 목소리(Windows 음성) — AllowVoiceFallback을 켰을 때만 주어진다.</summary>
    public interface IFallbackSpeaker
    {
        bool SayAndWait(string text);
    }

    /// <summary>
    /// 한 목소리 발화 — VoiceRoute가 세션 목소리로 찾은 소리를 재생하고, 없으면
    /// 말하지 않는다 (VoiceDropLog에 기록). 재생이 실패해도 다른 소스로 다시
    /// 시도하지 않는다. fallback은 설정을 켰을 때만 null이 아니다.
    ///
    /// 겹침 방지 규칙: 이 프로세스의 모든 발화는 PlaybackGate 하나를 지나고,
    /// 출력 경로 모두 "소리가 끝날 때까지" 돌아오지 않는다. 그래서 엔진
    /// 전환 중의 이전/새 싱크, [테스트 발화]까지 한 줄로 선다.
    /// </summary>
    public sealed class RadioVoice : IVoiceSink
    {
        // 인스턴스가 아니라 프로세스 단위 — 재시작으로 싱크가 바뀌어도 공유
        private static readonly object PlaybackGate = new object();

        private readonly VoiceRoute _route;
        private readonly IRadioPlayer _player;
        private readonly IFallbackSpeaker _fallback;
        private volatile bool _stopped;

        public RadioVoice(VoiceRoute route, IRadioPlayer player, IFallbackSpeaker fallback)
        {
            _route = route;
            _player = player;
            _fallback = fallback;
            Drops = new VoiceDropLog();
            route.HasFallbackSpeaker = fallback != null;
        }

        /// <summary>
        /// 플러그인과 셀프테스트가 같은 규칙으로 만든다 — Windows 음성은 섞임 허용(en)일 때만
        /// 만들어 넘긴다. RadioVoice는 받은 대체 발화기를 그대로 쓰므로 이 판단이 유일한 문이다.
        /// </summary>
        public static RadioVoice Create(VoiceRoute route, IRadioPlayer player, bool allowSetting,
                                        bool ko, Func<IFallbackSpeaker> fallbackSpeaker)
        {
            IFallbackSpeaker fb = VoicePolicy.FallbackAllowed(allowSetting, ko) && fallbackSpeaker != null
                ? fallbackSpeaker() : null;
            return new RadioVoice(route, player, fb);
        }

        public VoiceKind Kind { get { return _route.Kind; } }
        public VoiceRoute Route { get { return _route; } }
        public VoiceDropLog Drops { get; private set; }

        /// <summary>마지막으로 재생한 소스 (KokoroFx… / "sapi").</summary>
        public string LastSource { get; private set; }

        /// <summary>마지막으로 말하지 못한 사유.</summary>
        public string LastReason { get; private set; }

        public SpeakResult Speak(string text, string tone, bool urgent)
        {
            if (_stopped || string.IsNullOrEmpty(text))
                return SpeakResult.Stopped;
            // 파일 확인·조립·합성은 게이트 밖에서 — 앞 콜 재생 중에 준비해 둔다
            string reason;
            VoiceClip clip = _route.Resolve(text, tone, out reason);
            lock (PlaybackGate)
            {
                if (_stopped)
                    return SpeakResult.Stopped;   // 엔진 전환 중 — 이전 싱크는 더 말하지 않는다
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
                    // Stop()으로 끊긴 재생을 다른 목소리로 다시 읽지 않는다
                    if (_stopped)
                        return SpeakResult.Stopped;
                    reason = "play-failed";
                }
                if (_fallback != null)
                {
                    _fallback.SayAndWait(text);   // 동기 — 다음 콜과 겹치지 않게
                    LastSource = "sapi";
                    return SpeakResult.Played;
                }
                Drops.Record(VoicePolicy.KindName(_route.Kind), reason ?? "?", tone, text, urgent);
                LastReason = reason ?? "?";
                return SpeakResult.Dropped;
            }
        }

        /// <summary>
        /// 엔진 전환/종료 — 더 이상 말하지 않고, 재생 중인 wav/mp3는 즉시 끊는다.
        /// </summary>
        public void Stop()
        {
            _stopped = true;
            _player.Stop();
        }
    }
}
