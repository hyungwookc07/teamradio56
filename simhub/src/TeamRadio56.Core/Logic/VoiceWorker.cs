using System;
using System.Collections.Generic;
using System.Threading;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Logic
{
    /// <summary>발화 출력 계층 — 플러그인이 구현 (오디오 재생/TTS 폴백).</summary>
    public interface IVoiceSink
    {
        /// <summary>완성된 멘트를 소리로 낸다. 블로킹해도 된다 (전용 스레드).</summary>
        void Speak(string text, string tone, bool urgent);
    }

    /// <summary>
    /// tts.py VoiceWorker의 비-LLM 경로 포팅 — 버스에서 이벤트를 꺼내
    /// 렌더러로 텍스트를 만들고 싱크로 보낸다. 전용 백그라운드 스레드.
    /// </summary>
    public sealed class VoiceWorker : IDisposable
    {
        private const int RecentKept = 8;

        private readonly EventBus _bus;
        private readonly VoiceRenderer _renderer;
        private readonly IVoiceSink _sink;
        private readonly Queue<string> _recent = new Queue<string>();
        private readonly object _recentGate = new object();
        private Thread _thread;
        private volatile bool _stop;

        public bool Enabled = true;

        public VoiceWorker(EventBus bus, IVoiceSink sink, VoiceRenderer renderer = null)
        {
            _bus = bus;
            _sink = sink;
            _renderer = renderer ?? new VoiceRenderer();
        }

        public void Start()
        {
            if (_thread != null && _thread.IsAlive)
                return;
            _stop = false;
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "teamradio56-voice",
            };
            _thread.Start();
        }

        public string[] RecentCalls()
        {
            lock (_recentGate)
            {
                return _recent.ToArray();
            }
        }

        private void Loop()
        {
            while (!_stop)
            {
                RadioEvent ev;
                try
                {
                    ev = _bus.Pop(0.5);
                }
                catch (Exception ex)
                {
                    FileLog.Error("보이스 워커 pop 실패", ex);
                    continue;
                }
                if (ev == null)
                    continue;
                try
                {
                    Handle(ev);
                }
                catch (Exception ex)
                {
                    FileLog.Error("멘트 처리 중 오류 (" + ev.Type + ")", ex);
                }
            }
        }

        private void Handle(RadioEvent ev)
        {
            KeyValuePair<string, string> rendered = _renderer.TextFor(ev);
            string text = rendered.Key;
            if (string.IsNullOrEmpty(text))
                return;
            FileLog.Info("[크루치프] " + text);
            lock (_recentGate)
            {
                _recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
                while (_recent.Count > RecentKept)
                    _recent.Dequeue();
            }
            if (!Enabled || _sink == null)
                return;
            _sink.Speak(text, ev.Tone ?? "casual", ev.Priority == Priority.Critical);
        }

        public void Dispose()
        {
            _stop = true;
            Thread t = _thread;
            if (t != null && t.IsAlive)
                t.Join(2000);
        }
    }
}
