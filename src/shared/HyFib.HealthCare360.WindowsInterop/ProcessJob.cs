using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HyFib.HealthCare360.WindowsInterop;

/// <summary>
/// A Windows job object that ends every process in it when it is disposed, or when the process that owns it exits
/// (crash or kill included). Used for helper programs the server starts, such as the headless browser that makes
/// PDFs, so that none is left running without the server. Child processes the helper starts join the job too.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly SafeJobHandle _handle;

    private ProcessJob(SafeJobHandle handle) => _handle = handle;

    /// <summary>A new job that kills its processes on close. Null when not on Windows or the job can't be made.</summary>
    public static ProcessJob? TryCreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var limits = new JobObjectExtendedLimit { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimit>()))
        {
            handle.Dispose();
            return null;
        }

        return new ProcessJob(handle);
    }

    /// <summary>Puts a running process in the job. Processes it starts from now on join the job as well.</summary>
    /// <exception cref="Win32Exception">Windows refused, for example because the process has already exited.</exception>
    public void Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Ends every process in the job and waits, up to <paramref name="timeout"/>, until none is still running, so the
    /// files they had open are free. Returns false when some were still running at the end of the wait.
    /// </summary>
    public async Task<bool> TerminateAndWaitAsync(TimeSpan timeout)
    {
        TerminateJobObject(_handle, 1);
        var started = Stopwatch.GetTimestamp();
        while (ActiveProcesses() > 0)
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    /// <summary>Ends every process still in the job.</summary>
    public void Dispose() => _handle.Dispose();

#pragma warning disable
    // TEMPORARY, diagnostics branch only.
    public bool Contains(int processId)
    {
        var process = OpenProcess(0x1000, false, processId);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        IsProcessInJob(process, _handle, out var result);
        CloseHandle(process);
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, SafeJobHandle job, out bool result);
#pragma warning restore

    private uint ActiveProcesses()
    {
        var length = (uint)Marshal.SizeOf<JobObjectBasicAccounting>();
        return QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out var info, length, 0)
            ? info.ActiveProcesses
            : 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref JobObjectExtendedLimit information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job, int informationClass, out JobObjectBasicAccounting info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimit
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
    private struct JobObjectBasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public JobObjectBasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
