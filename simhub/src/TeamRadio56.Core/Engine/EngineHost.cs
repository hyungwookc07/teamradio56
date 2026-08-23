using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Engine
{
    /// <summary>
    /// 파이썬 엔진 프로세스 관리 (엔진 모드).
    ///
    /// C# 분석기 이식이 끝나기 전까지, 이미 실차 검증된 파이썬 엔진을
    /// 플러그인이 자식 프로세스로 띄워 전체 기능을 제공한다.
    /// 통신은 파일 두 개로 한다 — 설정(플러그인→엔진), 상태(엔진→플러그인).
    /// 소켓/파이프를 쓰지 않으므로 프로토콜도, 의존성도 없다.
    ///
    /// 프로세스 수명은 SimHub에 묶는다: 시작 시 고아 프로세스를 정리하고,
    /// End()에서 반드시 종료시킨다 (백그라운드에 남으면 소리가 두 번 난다).
    /// </summary>
    public sealed class EngineHost : IDisposable
    {
        private const int StopWaitMs = 4000;   // 디브리핑/저장 마무리 시간

        private readonly object _gate = new object();
        private Process _process;
        private StreamWriter _log;

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    try
                    {
                        return _process != null && !_process.HasExited;
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>마지막 실패 사유 (설정 화면에 표시).</summary>
        public string LastError { get; private set; }

        public static string DefaultExePath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir ?? ".", "teamradio56-engine", "teamradio56.exe");
            }
            catch (Exception)
            {
                return "teamradio56.exe";
            }
        }

        public static string EngineLogPath()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir ?? ".", "teamradio56.engine.log");
            }
            catch (Exception)
            {
                return "teamradio56.engine.log";
            }
        }

        /// <summary>
        /// 엔진을 띄운다. 이미 돌고 있으면 아무것도 하지 않는다.
        /// </summary>
        public bool Start(string exe, string extraArgs, string settingsPath, string statusPath)
        {
            lock (_gate)
            {
                if (IsRunning)
                    return true;

                LastError = null;
                if (string.IsNullOrEmpty(exe))
                {
                    LastError = "엔진 실행 파일 경로가 비어 있습니다";
                    return false;
                }
                if (!File.Exists(exe))
                {
                    LastError = "엔진을 찾을 수 없습니다: " + exe;
                    FileLog.Warn(LastError);
                    return false;
                }

                KillOrphans(exe);

                var args = new StringBuilder();
                if (!string.IsNullOrEmpty(extraArgs))
                    args.Append(extraArgs).Append(' ');
                args.Append("--settings \"").Append(settingsPath).Append("\" ");
                args.Append("--status-file \"").Append(statusPath).Append("\"");

                var info = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args.ToString(),
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                try
                {
                    OpenEngineLog();
                    _process = new Process { StartInfo = info, EnableRaisingEvents = true };
                    _process.OutputDataReceived += (s, e) => WriteEngineLog(e.Data);
                    _process.ErrorDataReceived += (s, e) => WriteEngineLog(e.Data);
                    _process.Exited += (s, e) =>
                        FileLog.Info("엔진 프로세스 종료됨");

                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                    FileLog.Info("엔진 시작: {0} {1}", exe, args);
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = "엔진 실행 실패: " + ex.Message;
                    FileLog.Error("엔진 실행 실패", ex);
                    _process = null;
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_process == null)
                {
                    CloseEngineLog();
                    return;
                }
                try
                {
                    if (!_process.HasExited)
                    {
                        // 엔진은 종료 시 디브리핑/레이스 저장을 한다 — 잠깐 기다렸다 강제 종료
                        FileLog.Info("엔진 종료 요청");
                        _process.Kill();
                        _process.WaitForExit(StopWaitMs);
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Error("엔진 종료 실패", ex);
                }
                finally
                {
                    try { _process.Dispose(); } catch (Exception) { }
                    _process = null;
                    CloseEngineLog();
                }
            }
        }

        /// <summary>
        /// 이전 실행이 비정상 종료돼 남은 엔진 프로세스를 정리한다.
        /// 남아 있으면 같은 콜이 두 번 들린다.
        /// </summary>
        private void KillOrphans(string exe)
        {
            string name;
            try
            {
                name = Path.GetFileNameWithoutExtension(exe);
            }
            catch (Exception)
            {
                return;
            }
            if (string.IsNullOrEmpty(name))
                return;

            try
            {
                Process[] found = Process.GetProcessesByName(name);
                foreach (Process p in found)
                {
                    try
                    {
                        FileLog.Warn("이전 엔진 프로세스 정리 (PID " + p.Id + ")");
                        p.Kill();
                        p.WaitForExit(2000);
                    }
                    catch (Exception)
                    {
                        // 접근 불가/이미 종료 — 무시
                    }
                    finally
                    {
                        try { p.Dispose(); } catch (Exception) { }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("고아 프로세스 정리 실패", ex);
            }
        }

        // -- 엔진 출력 로그 ---------------------------------------------------

        private void OpenEngineLog()
        {
            try
            {
                CloseEngineLog();
                // 매 실행마다 새로 (엔진 로그는 초당 여러 줄이라 누적하면 커진다)
                _log = new StreamWriter(EngineLogPath(), false, Encoding.UTF8);
                _log.AutoFlush = true;
            }
            catch (Exception)
            {
                _log = null;
            }
        }

        private void WriteEngineLog(string line)
        {
            if (line == null)
                return;
            lock (_gate)
            {
                try
                {
                    if (_log != null)
                        _log.WriteLine(line);
                }
                catch (Exception)
                {
                    _log = null;
                }
            }
        }

        private void CloseEngineLog()
        {
            try
            {
                if (_log != null)
                {
                    _log.Dispose();
                    _log = null;
                }
            }
            catch (Exception)
            {
                _log = null;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
