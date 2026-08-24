using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Threading;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// 1단계 오디오 경로 확인용 — Windows 내장 TTS.
    ///
    /// 발화는 전용 백그라운드 스레드에서 동기 Speak()로 한다.
    /// SpeakAsync는 SimHub의 WPF/스레딩 환경에서 소리 없이 씹히는 경우가
    /// 있고, 실패해도 예외가 호출자에게 오지 않아 원인을 알 수 없다.
    /// 동기 호출이면 실패가 그 자리에서 예외로 잡히고, 전용 스레드라
    /// 발화가 끝날 때까지 막혀도 UI에는 영향이 없다.
    ///
    /// 2단계에서 edge-tts(사전 캐시) + 무전기 효과로 교체된다.
    /// </summary>
    public sealed class SpeechOutput : IDisposable
    {
        private readonly Queue<string> _queue = new Queue<string>();
        private readonly object _gate = new object();
        private Thread _thread;
        private bool _stop;

        public void Say(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            lock (_gate)
            {
                EnsureThread();
                // 무전은 최신이 우선 — 밀린 발화는 버린다
                _queue.Clear();
                _queue.Enqueue(text);
                Monitor.Pulse(_gate);
            }
            FileLog.Info("발화 요청: " + text);
        }

        private void EnsureThread()
        {
            if (_thread != null && _thread.IsAlive)
                return;
            _stop = false;
            _thread = new Thread(Worker)
            {
                IsBackground = true,
                Name = "teamradio56-tts",
            };
            // System.Speech(SAPI)는 COM 기반 — STA가 안전하다
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        private void Worker()
        {
            SpeechSynthesizer synth = null;
            int failures = 0;
            try
            {
                while (true)
                {
                    string text;
                    lock (_gate)
                    {
                        while (_queue.Count == 0 && !_stop)
                            Monitor.Wait(_gate);
                        if (_stop)
                            return;
                        text = _queue.Dequeue();
                    }

                    try
                    {
                        if (synth == null)
                            synth = CreateSynth();
                        if (synth == null)
                        {
                            // TTS 자체를 못 만드는 환경 — 버튼이 눌렸다는
                            // 신호라도 주기 위해 시스템 비프를 낸다
                            System.Media.SystemSounds.Beep.Play();
                            continue;
                        }
                        synth.Speak(text);
                        failures = 0;
                        FileLog.Info("발화 완료: " + text);
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        FileLog.Error("TTS 발화 실패 (연속 " + failures + "회): " + text, ex);
                        // 다음 발화 때 새로 만든다 (장치 변경 등에서 복구)
                        try { if (synth != null) synth.Dispose(); } catch (Exception) { }
                        synth = null;
                        try { System.Media.SystemSounds.Beep.Play(); } catch (Exception) { }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("TTS 스레드 비정상 종료", ex);
            }
            finally
            {
                try { if (synth != null) synth.Dispose(); } catch (Exception) { }
            }
        }

        private static SpeechSynthesizer CreateSynth()
        {
            try
            {
                var synth = new SpeechSynthesizer();

                // 설치된 음성 목록 — 0개면 어떤 발화도 소리가 안 난다
                var names = new List<string>();
                foreach (InstalledVoice v in synth.GetInstalledVoices())
                {
                    names.Add(v.VoiceInfo.Name + (v.Enabled ? "" : " (비활성)"));
                }
                if (names.Count == 0)
                {
                    FileLog.Warn("설치된 Windows TTS 음성이 없습니다 — " +
                        "설정 > 시간 및 언어 > 음성에서 음성을 설치해야 소리가 납니다");
                }
                else
                {
                    FileLog.Info("설치된 TTS 음성 {0}개: {1}",
                        names.Count, string.Join(", ", names));
                }

                // 영어 무전이므로 영어 음성이 있으면 그걸 고른다
                try
                {
                    synth.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0,
                        new System.Globalization.CultureInfo("en-US"));
                }
                catch (Exception)
                {
                    // 영어 음성 없음 — 기본 음성 그대로 (알아듣기는 어려워도 소리는 난다)
                }

                synth.SetOutputToDefaultAudioDevice();
                synth.Volume = 100;
                FileLog.Info("내장 TTS 준비됨 (음성: {0})",
                    synth.Voice != null ? synth.Voice.Name : "기본");
                return synth;
            }
            catch (Exception ex)
            {
                FileLog.Error("TTS 초기화 실패", ex);
                return null;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stop = true;
                Monitor.Pulse(_gate);
            }
            Thread t = _thread;
            if (t != null && t.IsAlive)
                t.Join(2000);
        }
    }
}
