using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HyFib.HealthCare360.WindowsInterop;

/// <summary>
/// A Windows job object that ends every process in it when it is disposed, or when the process that owns it exits
/// (crash or kill included). Used for helper programs the server starts, such as the headless browser that makes
/// PDFs, so that none is left running without the server. A program started with <see cref="Start"/> is in the job
/// from its first instruction, and so is every process it starts.
/// </summary>
public sealed class ProcessJob : IDisposable
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int ErrorMoreData = 234;
    private const uint Synchronize = 0x0010_0000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WaitTimeout = 0x102;
    private const uint CreateNoWindow = 0x0800_0000;
    private const uint ExtendedStartupInfoPresent = 0x0008_0000;
    private const int StartfUseStdHandles = 0x100;
    private const nint ProcThreadAttributeHandleList = 0x0002_0002;
    private const nint ProcThreadAttributeJobList = 0x0002_000D;

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

    /// <summary>
    /// Starts a program inside the job, without a window. Windows puts it in the job before its first instruction, so
    /// every process it starts is in the job too. (A process put in a job once it is running can start others before
    /// that, and those stay outside.) The program inherits only <paramref name="inheritedHandles"/>. Its standard output
    /// and standard error go to <paramref name="output"/>, or nowhere when that is null, and its standard input is empty.
    /// </summary>
    /// <exception cref="Win32Exception">Windows could not start the program.</exception>
    /// <exception cref="InvalidOperationException">The program exited as soon as it started.</exception>
    public Process Start(string fileName, IEnumerable<string> arguments, SafeHandle? output = null, IReadOnlyCollection<SafeHandle>? inheritedHandles = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var commandLine = new StringBuilder();
        AppendArgument(commandLine, fileName);
        foreach (var argument in arguments)
        {
            commandLine.Append(' ');
            AppendArgument(commandLine, argument);
        }

        commandLine.Append('\0');

        // With STARTF_USESTDHANDLES, each standard stream needs a valid handle that the program inherits. A stream the
        // caller does not give is the NUL device: reads find it empty and writes go nowhere.
        using var nothing = File.OpenHandle(@"\\.\NUL", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Inheritable);
        var standardOutput = output ?? nothing;
        var inherited = (inheritedHandles ?? []).Append(standardOutput).Append(nothing).Distinct().ToList();

        // CreateProcess reads the handle and job lists through these pointers, so they stay put until it returns.
        var referenced = new List<SafeHandle>();
        var handleList = IntPtr.Zero;
        var jobList = IntPtr.Zero;
        var attributes = IntPtr.Zero;
        const int attributeCount = 2;
        nint attributeSize = 0;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attributeSize);
        try
        {
            handleList = Marshal.AllocHGlobal(IntPtr.Size * inherited.Count);
            jobList = Marshal.AllocHGlobal(IntPtr.Size);
            attributes = Marshal.AllocHGlobal(attributeSize);
            foreach (var handle in inherited.Append(_handle))
            {
                var added = false;
                handle.DangerousAddRef(ref added);
                if (added)
                {
                    referenced.Add(handle);
                }
            }

            for (var i = 0; i < inherited.Count; i++)
            {
                Marshal.WriteIntPtr(handleList, i * IntPtr.Size, inherited[i].DangerousGetHandle());
            }

            Marshal.WriteIntPtr(jobList, _handle.DangerousGetHandle());
            if (!InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref attributeSize))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handleList, IntPtr.Size * inherited.Count, IntPtr.Zero, IntPtr.Zero)
                    || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeJobList, jobList, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                var startup = new StartupInfoEx { AttributeList = attributes };
                startup.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
                startup.StartupInfo.Flags = StartfUseStdHandles;
                startup.StartupInfo.StdInput = nothing.DangerousGetHandle();
                startup.StartupInfo.StdOutput = standardOutput.DangerousGetHandle();
                startup.StartupInfo.StdError = standardOutput.DangerousGetHandle();
                if (!CreateProcessW(fileName, commandLine.ToString().ToCharArray(), IntPtr.Zero, IntPtr.Zero, true,
                    CreateNoWindow | ExtendedStartupInfoPresent, IntPtr.Zero, null, ref startup, out var created))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                CloseHandle(created.Thread);
                return Adopt(fileName, created);
            }
            finally
            {
                DeleteProcThreadAttributeList(attributes);
            }
        }
        finally
        {
            referenced.ForEach(handle => handle.DangerousRelease());
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(jobList);
            Marshal.FreeHGlobal(handleList);
        }
    }

    /// <summary>
    /// Ends every process in the job and waits, up to <paramref name="timeout"/>, until each has exited and so has
    /// closed the files it had open. Returns false when some were still exiting at the end of the wait.
    /// </summary>
    public async Task<bool> TerminateAndWaitAsync(TimeSpan timeout)
    {
        // The job stops counting a process the moment it is ended, but the process closes its files only once it has
        // finished exiting, which on a busy machine takes seconds. The job no longer lists it by then, so its processes
        // are opened before they are ended, and waited on.
        var processes = OpenProcesses();
        try
        {
            TerminateJobObject(_handle, 1);
            var started = Stopwatch.GetTimestamp();
            while (ActiveProcesses() > 0 || processes.Exists(process => WaitForSingleObject(process, 0) == WaitTimeout))
            {
                if (Stopwatch.GetElapsedTime(started) >= timeout)
                {
                    return false;
                }

                await Task.Delay(50);
            }

            return true;
        }
        finally
        {
            processes.ForEach(process => process.Dispose());
        }
    }

    /// <summary>Ends every process still in the job.</summary>
    public void Dispose() => _handle.Dispose();

    // The Process stands for the started program. It opens a handle of its own before CreateProcess's is closed, so the
    // process id cannot pass to another process in between.
    private static Process Adopt(string fileName, ProcessInformation created)
    {
        using var process = new SafeProcessHandle(created.Process, ownsHandle: true);
        Process? started = null;
        try
        {
            started = Process.GetProcessById(created.ProcessId);
            _ = started.SafeHandle;
            return started;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            started?.Dispose();
            throw new InvalidOperationException($"{fileName} exited as soon as it started.", ex);
        }
    }

    // How Process.Start quotes an argument: CommandLineToArgvW and the C runtime split the line back the same way.
    private static void AppendArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are taken as they are, except before a quote: those are doubled and the quote escaped.
            line.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            line.Append(c);
            backslashes = 0;
        }

        // The closing quote counts as a quote, so backslashes before it are doubled too.
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    private uint ActiveProcesses()
    {
        var length = (uint)Marshal.SizeOf<JobObjectBasicAccounting>();
        return QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out var info, length, 0)
            ? info.ActiveProcesses
            : 0;
    }

    // The processes in the job now. A process id passes to another process once its own has gone, so only processes
    // that are still in this job when opened are kept.
    private List<SafeProcessHandle> OpenProcesses()
    {
        var processes = new List<SafeProcessHandle>();
        foreach (var id in ProcessIds())
        {
            var process = OpenProcess(Synchronize | ProcessQueryLimitedInformation, false, id);
            if (!process.IsInvalid && IsProcessInJob(process, _handle, out var inJob) && inJob)
            {
                processes.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return processes;
    }

    private List<int> ProcessIds()
    {
        // JOBOBJECT_BASIC_PROCESS_ID_LIST: the number of processes in the job and the number listed, then the ids.
        for (var capacity = 64; capacity <= 16384; capacity *= 4)
        {
            var length = 8 + (capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (QueryInformationJobObject(_handle, JobObjectBasicProcessIdList, buffer, (uint)length, IntPtr.Zero))
                {
                    var listed = Marshal.ReadInt32(buffer, 4);
                    return [.. Enumerable.Range(0, listed).Select(i => (int)Marshal.ReadIntPtr(buffer, 8 + (i * IntPtr.Size)))];
                }

                if (Marshal.GetLastPInvokeError() != ErrorMoreData)
                {
                    return [];
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref JobObjectExtendedLimit information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeJobHandle job, int informationClass, out JobObjectBasicAccounting info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass, IntPtr info, uint length, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeJobHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList, uint flags, nint attribute, IntPtr value, nint size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

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

    // STARTUPINFOW
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    // STARTUPINFOEXW
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    // PROCESS_INFORMATION
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
