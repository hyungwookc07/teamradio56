using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TeamRadio56.Core.Diagnostics
{
    /// <summary>
    /// 자체 로그 파일 (DLL 옆 teamradio56.log).
    ///
    /// SimHub 로그 API에 의존하지 않는 독립 진단 채널 — 플러그인이 로드조차
    /// 안 되는 상황을 빼면 항상 기록이 남는다. 문제 생기면 이 파일을 보면 된다.
    /// </summary>
    public static class FileLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _failed;

        public static string Path
        {
            get
            {
                if (_path == null)
                {
                    try
                    {
                        string dir = System.IO.Path.GetDirectoryName(
                            Assembly.GetExecutingAssembly().Location);
                        _path = System.IO.Path.Combine(dir ?? ".", "teamradio56.log");
                    }
                    catch (Exception)
                    {
                        _path = "teamradio56.log";
                    }
                }
                return _path;
            }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", ex == null ? message : message + " :: " + ex);
        }

        public static void Info(string format, params object[] args)
        {
            Write("INFO", SafeFormat(format, args));
        }

        private static string SafeFormat(string format, object[] args)
        {
            try
            {
                return string.Format(format, args);
            }
            catch (Exception)
            {
                return format;
            }
        }

        private static void Write(string level, string message)
        {
            if (_failed)
                return;
            lock (Gate)
            {
                try
                {
                    string line = string.Format("{0:yyyy-MM-dd HH:mm:ss} {1,-5} {2}{3}",
                        DateTime.Now, level, message, Environment.NewLine);
                    File.AppendAllText(Path, line, Encoding.UTF8);
                }
                catch (Exception)
                {
                    _failed = true;   // 쓰기 불가(권한 등) — 로그 때문에 앱이 죽지 않게
                }
            }
        }

        /// <summary>세션 시작 시 헤더 (이전 실행과 구분).</summary>
        public static void Banner(string version)
        {
            Write("INFO", "==== teamradio56 " + version + " 시작 ====");
        }
    }
}
