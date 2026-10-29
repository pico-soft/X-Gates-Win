using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XGates.Core.Libv2ray;

/// <summary>
/// Windows Job Object с JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: все xray.exe, привязанные к нему, ОС убивает сама, когда
/// закрывается последний хэндл — т.е. когда умирает наш процесс (в т.ч. при падении/«Снять задачу»). Иначе после краха
/// оставались бы «сироты» xray.exe, держащие порты 10815/10816 (критерий MVP: выход не оставляет xray.exe).
/// На других ОС — no-op (Linux используется только для разработки и тестов).
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(CreateJob);

    public static void Attach(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        var job = Job.Value;
        if (job == IntPtr.Zero) return;
        if (!AssignProcessToJobObject(job, p.Handle))
            Trace.TraceWarning("AssignProcessToJobObject failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }

    private static IntPtr CreateJob()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        int len = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)len))
                Trace.TraceWarning("SetInformationJobObject failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job; // хэндл живёт до конца процесса — намеренно не закрываем
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

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
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);
}
