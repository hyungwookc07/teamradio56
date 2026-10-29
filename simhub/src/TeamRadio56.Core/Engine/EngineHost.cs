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
        // SimHub 수명 동안 하나 — 핸들이 닫히면(SimHub 종료/크래시) 커널이
        // 엔진 트리를 통째로 죽인다. 생성 실패 시 null (Kill 폴백).
        private ProcessJob _job;
        private bool _inJob;     // 현재 _process가 작업 개체에 들어갔는가

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
            if (IsRunning)
                return true;
            // WMI 검색은 수 초 걸릴 수 있다 — _gate 밖에서 (IsRunning을 읽는
            // UI/데이터 스레드를 막지 않게)
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                KillOrphans(exe, statusPath);

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
                // 엔진이 SimHub의 종료를 스스로 감지하게 — 작업 개체가 실패해도
                // 고아로 남지 않는 2차 안전망. 인자가 아니라 환경변수인 이유:
                // 이 인자를 모르는 구버전 엔진(번들 exe)이 argparse 오류로 죽는다
                try
                {
                    info.EnvironmentVariables["TEAMRADIO56_PARENT_PID"] =
                        CurrentPid().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception) { }

                try
                {
                    OpenEngineLog();
                    _process = new Process { StartInfo = info, EnableRaisingEvents = true };
                    _process.OutputDataReceived += (s, e) => WriteEngineLog(e.Data);
                    _process.ErrorDataReceived += (s, e) => WriteEngineLog(e.Data);
                    _process.Exited += (s, e) =>
                        FileLog.Info("엔진 프로세스 종료됨");

                    _process.Start();
                    if (_job == null)
                        _job = ProcessJob.TryCreate();
                    _inJob = _job != null && _job.Assign(_process);
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();
                    FileLog.Info("엔진 시작: {0} {1}", exe, args);
                    if (!_inJob)
                        FileLog.Warn("엔진을 작업 개체에 묶지 못함 — SimHub 비정상 종료 시 "
                                     + "엔진의 --parent-pid 감시에 의존");
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
                        FileLog.Info("엔진 종료 요청");
                        // 트리 전체 종료 — 래퍼(.bat/pipenv/onefile) 뒤의 실제
                        // 엔진까지. 작업 개체가 없으면 직계만 Kill (폴백)
                        // 작업에 못 들어간 프로세스는 TerminateJobObject가 (빈 작업에
                        // 대해) 성공을 반환해도 살아 있다 — 반드시 직접 Kill
                        if (!_inJob || _job == null || !_job.TerminateAll())
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
                    _inJob = false;
                    CloseEngineLog();
                }
            }
        }

        /// <summary>
        /// 이전 실행이 비정상 종료돼 남은 엔진 프로세스를 정리한다.
        /// 남아 있으면 같은 콜이 두 번(서로 다른 목소리로) 들린다.
        ///
        /// 소스 실행은 실행 파일이 범용 python.exe라 이름으로 찾으면 PC의 모든
        /// 파이썬을 죽이게 된다 — 대신 명령줄에 이 플러그인의 상태 파일 경로가
        /// 들어 있는 프로세스만 고른다 (플러그인이 띄운 엔진만 그 인자를 받는다).
        /// 묶인 실행 파일(teamradio56.exe)은 기존처럼 이름으로도 정리한다.
        /// </summary>
        public void KillOrphans(string exe, string statusPath)
        {
            int killed = KillByCommandLine(statusPath);

            string name = null;
            try
            {
                name = string.IsNullOrEmpty(exe) ? null : Path.GetFileNameWithoutExtension(exe);
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(name) || IsInterpreter(name))
                return;

            try
            {
                Process[] found = Process.GetProcessesByName(name);
                foreach (Process p in found)
                {
                    try
                    {
                        if (_process != null && p.Id == _process.Id)
                            continue;
                        FileLog.Warn("이전 엔진 프로세스 정리 (PID " + p.Id + ")");
                        p.Kill();
                        p.WaitForExit(2000);
                        killed++;
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
            if (killed > 0)
                FileLog.Warn("고아 엔진 " + killed + "개 정리됨 (두 목소리 겹침 원인)");
        }

        private static bool IsInterpreter(string name)
        {
            return name.Equals("python", StringComparison.OrdinalIgnoreCase)
                || name.Equals("pythonw", StringComparison.OrdinalIgnoreCase)
                || name.Equals("py", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 명령줄에 "--status-file" 과 이 플러그인의 상태 파일 경로가 모두 든
        /// 프로세스를 종료 (WMI). 현재 추적 중인 자식은 건너뛴다.
        /// </summary>
        private int KillByCommandLine(string statusPath)
        {
            if (string.IsNullOrEmpty(statusPath))
                return 0;
#if NETFRAMEWORK
            int killed = 0;
            int self = CurrentPid();
            int tracked = -1;
            try
            {
                if (_process != null && !_process.HasExited)
                    tracked = _process.Id;
            }
            catch (Exception) { }
            try
            {
                var options = new System.Management.EnumerationOptions
                {
                    Timeout = TimeSpan.FromSeconds(5),   // WMI가 느려도 무한 대기 금지
                    ReturnImmediately = true,
                };
                using (var searcher = new System.Management.ManagementObjectSearcher(
                    new System.Management.ManagementScope(@"root\cimv2"),
                    new System.Management.ObjectQuery(
                        "SELECT ProcessId, CommandLine FROM Win32_Process "
                        + "WHERE CommandLine LIKE '%--status-file%'"),
                    options))
                using (System.Management.ManagementObjectCollection results = searcher.Get())
                {
                    foreach (System.Management.ManagementBaseObject mo in results)
                    {
                        try
                        {
                            string cmd = mo["CommandLine"] as string;
                            int pid = Convert.ToInt32(mo["ProcessId"]);
                            if (cmd == null || pid == self || pid == tracked)
                                continue;
                            if (cmd.IndexOf(statusPath, StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            using (Process p = Process.GetProcessById(pid))
                            {
                                FileLog.Warn("고아 엔진 정리 (PID " + pid + "): " + cmd);
                                p.Kill();
                                p.WaitForExit(2000);
                                killed++;
                            }
                        }
                        catch (Exception)
                        {
                            // 이미 종료/접근 불가 — 무시
                        }
                        finally
                        {
                            mo.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                FileLog.Error("고아 엔진 검색 실패 (WMI)", ex);
            }
            return killed;
#else
            return 0;
#endif
        }

        private static int CurrentPid()
        {
            try
            {
                using (Process me = Process.GetCurrentProcess())
                    return me.Id;
            }
            catch (Exception)
            {
                return 0;
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
            ProcessJob job = _job;
            _job = null;
            if (job != null)
                job.Dispose();
        }
    }
}
