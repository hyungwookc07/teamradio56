using System;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.SimHub
{
    /// <summary>
    /// mp3 재생 — SoundPlayer는 wav 전용이라, 런타임 edge 합성 결과(mp3)는
    /// WPF MediaPlayer로 재생한다. MediaPlayer는 디스패처가 있는 스레드가
    /// 필요하므로 전용 STA 스레드에서 Dispatcher.Run()을 돌린다.
    ///
    /// 겹침 방지: 재생 중인 플레이어를 필드로 붙잡아 두고(GC로 이벤트를 잃지
    /// 않게), 타임아웃/중단 시 반드시 Stop+Close 한다 — 끝났다고 보고
    /// 돌아왔는데 소리가 계속 나면 다음 콜과 겹친다.
    /// </summary>
    public sealed class Mp3Player : IDisposable
    {
        private Thread _thread;
        private Dispatcher _dispatcher;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
        private MediaPlayer _current;          // 디스패처 스레드에서만 접근
        private ManualResetEventSlim _currentDone;

        private void EnsureThread()
        {
            if (_dispatcher != null)
                return;
            lock (_ready)
            {
                if (_dispatcher != null)
                    return;
                _thread = new Thread(() =>
                {
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    _ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "teamradio56-mp3",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                _ready.Wait(3000);
            }
        }

        /// <summary>
        /// 동기 재생 (보이스 워커 전용 스레드에서 부른다). 실패 시 false.
        /// 열리지 않으면 openTimeoutMs, 열린 뒤엔 실제 길이 + 여유만큼 기다린다.
        /// </summary>
        public bool PlaySync(string path, double volume, int openTimeoutMs = 5000)
        {
            EnsureThread();
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher == null)
                return false;

            var done = new ManualResetEventSlim();
            var opened = new ManualResetEventSlim();
            bool ok = false;
            double durationMs = 0;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    StopCurrentOnDispatcher();
                    _stopRequested = false;
                    var player = new MediaPlayer();
                    _current = player;
                    _currentDone = done;
                    player.MediaOpened += (s, e) =>
                    {
                        if (player.NaturalDuration.HasTimeSpan)
                            durationMs = player.NaturalDuration.TimeSpan.TotalMilliseconds;
                        opened.Set();
                        player.Play();
                    };
                    player.MediaEnded += (s, e) =>
                    {
                        ok = true;
                        Finish(player);
                    };
                    player.MediaFailed += (s, e) =>
                    {
                        FileLog.Error("mp3 재생 실패: " + path,
                            e.ErrorException ?? new Exception("MediaFailed"));
                        Finish(player);
                    };
                    player.Volume = Math.Max(0.0, Math.Min(1.0, volume));
                    player.Open(new Uri(path, UriKind.Absolute));
                }
                catch (Exception ex)
                {
                    FileLog.Error("mp3 재생 초기화 실패: " + path, ex);
                    done.Set();
                }
            }));

            try
            {
                // 열림 또는 실패/종료 중 먼저 오는 쪽 — MediaFailed면 즉시 폴백
                int which = WaitHandle.WaitAny(
                    new[] { opened.WaitHandle, done.WaitHandle }, openTimeoutMs);
                if (which == WaitHandle.WaitTimeout)
                {
                    FileLog.Warn("mp3가 열리지 않음 — 중단: " + path);
                    StopCurrent();
                    return false;
                }
                if (which == 1)
                    return ok || _stopRequested;
                int playMs = durationMs > 0 ? (int)durationMs + 3000 : 30000;
                if (!done.Wait(playMs))
                {
                    FileLog.Warn("mp3 재생이 예상 길이를 넘김 — 중단: " + path);
                    StopCurrent();
                    return true;   // 이미 들렸다 — 같은 문장을 TTS로 다시 읽지 않는다
                }
                return ok || _stopRequested;
            }
            finally
            {
                opened.Dispose();
            }
        }

        // StopCurrent로 끊은 재생은 '실패'가 아니다 — 호출부가 같은 문장을
        // TTS로 다시 읽어 버리지 않게 성공으로 돌려준다
        private volatile bool _stopRequested;

        /// <summary>재생 중인 mp3를 즉시 멈춘다 (엔진 전환/종료 시).</summary>
        public void StopCurrent()
        {
            _stopRequested = true;
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher == null)
                return;
            try
            {
                dispatcher.Invoke(new Action(StopCurrentOnDispatcher),
                                  TimeSpan.FromSeconds(2));
            }
            catch (Exception) { }
        }

        private void StopCurrentOnDispatcher()
        {
            MediaPlayer player = _current;
            if (player != null)
                Finish(player);
        }

        private void Finish(MediaPlayer player)
        {
            try { player.Stop(); } catch (Exception) { }
            try { player.Close(); } catch (Exception) { }
            if (ReferenceEquals(_current, player))
            {
                _current = null;
                ManualResetEventSlim done = _currentDone;
                _currentDone = null;
                try { if (done != null) done.Set(); } catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            StopCurrent();
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher != null)
                dispatcher.InvokeShutdown();
            _dispatcher = null;
        }
    }
}
