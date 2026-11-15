using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiVideoEditor.Video;

/// <summary>
/// D060 §5: every ffmpeg the app starts belongs to one Windows job object that ends its processes when the app's process
/// ends — however it ends (a crash, the task manager), so no ffmpeg is ever left running behind the app. A normal close
/// cancels the export and waits for its cleanup first (the temporary files are deleted then); this is only the guarantee
/// beneath it. The job is created once; a system that refuses it (not Windows, a job that forbids nesting) leaves the
/// processes as before, with the app's own disposal ending them.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when the job exists (tests).</summary>
    public static bool IsAvailable => Job.Value != IntPtr.Zero;

    /// <summary>Puts <paramref name="process"/> (just started) in the job; false when it could not.</summary>
    public static bool Add(Process process)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var job = Job.Value;
        if (job == IntPtr.Zero) return false;
        try
        {
            return AssignProcessToJobObject(job, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;   // the process already exited
        }
    }

    /// <summary>Whether <paramref name="process"/> is in the app's job (tests).</summary>
    public static bool Contains(Process process) =>
        OperatingSystem.IsWindows() && Job.Value != IntPtr.Zero && IsProcessInJob(process.Handle, Job.Value, out var result) && result;

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose }
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, pointer, false);
                if (SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, (uint)size)) return job;
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
            CloseHandle(job);
            return IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    // The handle is never closed: the process's end closes it, and that ends the processes in the job.

    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformationClass = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);
}
