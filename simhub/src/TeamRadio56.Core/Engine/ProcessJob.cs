using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TeamRadio56.Core.Diagnostics;

namespace TeamRadio56.Core.Engine
{
    /// <summary>
    /// Windows 작업 개체(Job Object)로 엔진 프로세스 트리를 SimHub 수명에 묶는다.
    ///
    /// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: 이 핸들이 닫히면(SimHub이 정상이든
    /// 비정상이든 끝나면 커널이 닫는다) 작업 안의 모든 프로세스가 죽는다.
    /// .NET Framework의 Process.Kill()은 직계 자식 하나만 죽여서, 래퍼(.bat,
    /// pipenv.exe, PyInstaller onefile)로 띄운 실제 엔진이 살아남아 다음
    /// 엔진과 함께 두 목소리로 말하던 문제를 막는다.
    ///
    /// 실패해도(권한/중첩 작업 제한 등) 예외를 던지지 않는다 — 그 경우
    /// 호출부가 기존 Process.Kill 경로로 폴백한다.
    /// </summary>
    internal sealed class ProcessJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

        private IntPtr _handle;

        private ProcessJob(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>작업 개체를 만든다. 실패하면 null.</summary>
        public static ProcessJob TryCreate()
        {
            if (!IsWindows)
                return null;
            try
            {
                IntPtr h = CreateJobObject(IntPtr.Zero, null);
                if (h == IntPtr.Zero)
                    return null;
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(info, ptr, false);
                    if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    {
                        CloseHandle(h);
                        return null;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
                return new ProcessJob(h);
            }
            catch (Exception ex)
            {
                FileLog.Error("작업 개체 생성 실패 — 프로세스 Kill로 폴백", ex);
                return null;
            }
        }

        /// <summary>프로세스(와 이후 그 자손 전부)를 작업에 넣는다.</summary>
        public bool Assign(Process process)
        {
            if (_handle == IntPtr.Zero || process == null)
                return false;
            try
            {
                return AssignProcessToJobObject(_handle, process.Handle);
            }
            catch (Exception ex)
            {
                FileLog.Error("작업 개체 할당 실패", ex);
                return false;
            }
        }

        /// <summary>작업 안의 프로세스 트리 전체를 종료한다 (작업은 재사용 가능).</summary>
        public bool TerminateAll()
        {
            if (_handle == IntPtr.Zero)
                return false;
            try
            {
                return TerminateJobObject(_handle, 1);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose()
        {
            IntPtr h = _handle;
            _handle = IntPtr.Zero;
            if (h != IntPtr.Zero)
            {
                try { CloseHandle(h); } catch (Exception) { }
            }
        }

        private static bool IsWindows
        {
            get { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
        }

        // -- P/Invoke ---------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
                                                           IntPtr lpInfo, uint cbInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
