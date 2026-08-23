using System;
using System.Speech.Synthesis;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 1단계 오디오 경로 확인용 — Windows 내장 TTS.
    ///
    /// 목적은 "소리가 실제로 스피커로 나가는가"만 검증하는 것.
    /// 2단계에서 edge-tts(사전 캐시) + 무전기 효과로 교체된다.
    /// </summary>
    public sealed class SpeechOutput : IDisposable
    {
        private SpeechSynthesizer _synth;
        private bool _broken;

        public void Say(string text)
        {
            if (_broken || string.IsNullOrEmpty(text))
                return;
            try
            {
                if (_synth == null)
                {
                    _synth = new SpeechSynthesizer();
                    _synth.SetOutputToDefaultAudioDevice();
                    FileLog.Info("내장 TTS 준비됨 (음성: {0})",
                        _synth.Voice != null ? _synth.Voice.Name : "기본");
                }
                _synth.SpeakAsyncCancelAll();
                _synth.SpeakAsync(text);
                FileLog.Info("발화: " + text);
            }
            catch (Exception ex)
            {
                _broken = true;   // 오디오 장치 없음 등 — 앱은 계속 동작
                FileLog.Error("TTS 실패 (이후 무음으로 동작)", ex);
            }
        }

        public void Dispose()
        {
            try
            {
                if (_synth != null)
                {
                    _synth.SpeakAsyncCancelAll();
                    _synth.Dispose();
                    _synth = null;
                }
            }
            catch (Exception)
            {
                // 종료 중 예외는 무시
            }
        }
    }
}
