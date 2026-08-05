using System.Diagnostics;
using System.Runtime.InteropServices;
// 本命名空间就叫 ...Utils.System，裸写 System.Environment 会解析到自己身上
using SysEnvironment = System.Environment;

namespace VPetLLM.Utils.System
{
    /// <summary>
    /// 把子进程挂进一个 Job Object，宿主进程一消失，操作系统就顺手把它们杀掉。
    ///
    /// 为什么必须靠系统兜底：VPet 的退出流程最后是 <c>Environment.Exit(0)</c>，
    /// WPF 的 Application.Exit 事件根本不会触发，插件的清理代码一行都跑不到。
    /// mpv 是独立进程，父进程没了它照样把整句话播完 —— 表现就是"VPet 都退出了还在说话"。
    /// 崩溃、任务管理器强杀同理，任何靠"退出时记得清理"的写法都堵不住。
    ///
    /// Job 句柄是静态的，与宿主进程同生命周期：进程一终止，句柄被系统关闭，
    /// KILL_ON_JOB_CLOSE 就会把 Job 里所有还活着的进程一起带走。
    /// </summary>
    internal static class ChildProcessTracker
    {
        private static readonly IntPtr _jobHandle;
        private static readonly bool _available;

        static ChildProcessTracker()
        {
            try
            {
                // 名字带 PID：多开 VPet 时各自持有各自的 Job，互不影响
                _jobHandle = CreateJobObject(IntPtr.Zero, $"VPetLLM_child_{SysEnvironment.ProcessId}");
                if (_jobHandle == IntPtr.Zero)
                {
                    Logger.Log($"ChildProcessTracker: 创建 Job 失败 (err={Marshal.GetLastWin32Error()})，子进程将不受进程退出保护");
                    return;
                }

                var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
                var infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(extendedInfo, infoPtr, false);

                    if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                    {
                        Logger.Log($"ChildProcessTracker: 设置 Job 限制失败 (err={Marshal.GetLastWin32Error()})");
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }

                _available = true;
                Logger.Log("ChildProcessTracker: Job 已就绪，子进程将随宿主进程一同退出");
            }
            catch (Exception ex)
            {
                Logger.Log($"ChildProcessTracker: 初始化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 把进程登记进 Job。失败只记日志不抛 —— 拿不到系统兜底也不该影响播放本身。
        /// </summary>
        public static void Track(Process? process)
        {
            if (!_available || process is null)
                return;

            try
            {
                if (process.HasExited)
                    return;

                if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                {
                    Logger.Log($"ChildProcessTracker: 登记进程 {process.Id} 失败 (err={Marshal.GetLastWin32Error()})");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"ChildProcessTracker: 登记进程失败: {ex.Message}");
            }
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

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
    }
}
