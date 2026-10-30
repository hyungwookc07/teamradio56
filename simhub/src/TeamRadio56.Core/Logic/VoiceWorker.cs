using System;
using System.Collections.Generic;
using System.Threading;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Logic
{
    /// <summary>발화 출력 계층 — RadioVoice (세션 목소리 하나로 재생, 없으면 무음).</summary>
    public interface IVoiceSink
    {
        /// <summary>완성된 멘트를 소리로 낸다. 블로킹해도 된다 (전용 스레드).</summary>
        SpeakResult Speak(string text, string tone, bool urgent);
    }

    /// <summary>최근 무전 한 줄 — Dropped면 목소리를 섞지 않으려고 자막으로만 낸 콜.</summary>
    public sealed class RecentCall
    {
        public RecentCall(string time, string text)
        {
            Time = time ?? "";
            Text = text ?? "";
        }

        /// <summary>"HH:mm:ss" (상태 파일에서 못 읽으면 빈 문자열).</summary>
        public string Time { get; private set; }
        public string Text { get; private set; }
        public volatile bool Dropped;

        public string Line
        {
            get { return Time.Length > 0 ? Time + "  " + Text : Text; }
        }

        /// <summary>"HH:mm:ss  텍스트" 한 줄을 나눈다 (상태 파일·이전 형식).</summary>
        public static RecentCall Parse(string line, bool dropped = false)
        {
            string raw = line ?? "";
            int sep = raw.IndexOf("  ", StringComparison.Ordinal);
            RecentCall call = sep > 0
                ? new RecentCall(raw.Substring(0, sep), raw.Substring(sep + 2))
                : new RecentCall("", raw);
            call.Dropped = dropped;
            return call;
        }
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
        private readonly Queue<RecentCall> _recent = new Queue<RecentCall>();
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
            RecentCall[] items = RecentCallItems();
            var lines = new string[items.Length];
            for (int i = 0; i < items.Length; i++)
                lines[i] = items[i].Line;
            return lines;
        }

        public RecentCall[] RecentCallItems()
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
            FileLog.Info("[무전] " + text);
            var call = new RecentCall(DateTime.Now.ToString("HH:mm:ss"), text);
            lock (_recentGate)
            {
                _recent.Enqueue(call);
                while (_recent.Count > RecentKept)
                    _recent.Dequeue();
            }
            if (!Enabled || _sink == null)
                return;
            SpeakResult r = _sink.Speak(text, ev.Tone ?? "casual", ev.Priority == Priority.Critical);
            if (r == SpeakResult.Dropped)
                call.Dropped = true;
        }

        /// <summary>
        /// 워커를 멈춘다. 재생 중인 문장이 끝날 때까지 기다려야 새 엔진의 첫
        /// 콜과 겹치지 않는다 — 싱크를 먼저 Stop()하면 wav/mp3는 즉시 끊긴다.
        /// </summary>
        public void Dispose()
        {
            _stop = true;
            Thread t = _thread;
            if (t != null && t.IsAlive && !t.Join(10000))
                FileLog.Warn("보이스 워커가 10초 안에 멈추지 않음");
        }
    }
}
