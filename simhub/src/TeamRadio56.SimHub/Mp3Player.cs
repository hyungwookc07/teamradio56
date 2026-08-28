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
    /// </summary>
    public sealed class Mp3Player : IDisposable
    {
        private Thread _thread;
        private Dispatcher _dispatcher;
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();

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
        /// </summary>
        public bool PlaySync(string path, double volume, int timeoutMs = 30000)
        {
            EnsureThread();
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher == null)
                return false;

            var done = new ManualResetEventSlim();
            bool ok = false;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var player = new MediaPlayer();
                    player.MediaOpened += (s, e) => player.Play();
                    player.MediaEnded += (s, e) =>
                    {
                        ok = true;
                        player.Close();
                        done.Set();
                    };
                    player.MediaFailed += (s, e) =>
                    {
                        FileLog.Error("mp3 재생 실패: " + path,
                            e.ErrorException ?? new Exception("MediaFailed"));
                        player.Close();
                        done.Set();
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
            done.Wait(timeoutMs);
            return ok;
        }

        public void Dispose()
        {
            Dispatcher dispatcher = _dispatcher;
            if (dispatcher != null)
                dispatcher.InvokeShutdown();
            _dispatcher = null;
        }
    }
}
