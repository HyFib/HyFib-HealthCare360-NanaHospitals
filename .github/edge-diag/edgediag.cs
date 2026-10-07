#:package System.Management@10.0.12
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false
#:property NoWarn=CA1416

// Diagnostic: starts headless Edge the way HeadlessBrowser does, prints one PDF, closes it the way
// HeadlessBrowser.DisposeAsync does, and reports which processes carried the profile, whether each was in the job,
// when the main process exited and when its stdout/stderr reached EOF, and what still held profile files afterwards.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

var browserPath = Environment.GetEnvironmentVariable("DIAG_BROWSER") ?? @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
var iterations = int.Parse(Environment.GetEnvironmentVariable("DIAG_ITERATIONS") ?? "1", CultureInfo.InvariantCulture);
var load = int.Parse(Environment.GetEnvironmentVariable("DIAG_LOAD") ?? "0", CultureInfo.InvariantCulture);
var verbose = Environment.GetEnvironmentVariable("DIAG_VERBOSE") == "1";
using var stop = new CancellationTokenSource();
for (var i = 0; i < load; i++)
{
    new Thread(() => { long x = 0; while (!stop.IsCancellationRequested) { x++; } }) { IsBackground = true }.Start();
}

Console.WriteLine($"Edge {FileVersionInfo.GetVersionInfo(browserPath).ProductVersion} at {browserPath}; OS {Environment.OSVersion}; cpus {Environment.ProcessorCount}; load threads {load}; admin {Diag.IsAdmin()}; {Diag.OwnJob()}");
var failures = 0;
for (var i = 0; i < iterations; i++)
{
    if (!await Diag.RunOnce(browserPath, i, verbose))
    {
        failures++;
    }
}

Console.WriteLine($"{iterations - failures} of {iterations} runs left nothing behind");
stop.Cancel();

static class Diag
{
    private static readonly string[] Switches =
    [
        "--headless", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
        "--disable-component-extensions-with-background-pages", "--disable-background-networking", "--disable-sync",
        "--disable-default-apps", "--disable-breakpad", "--disable-domain-reliability", "--no-pings", "--mute-audio",
        "--disable-gpu", "--hide-scrollbars",
    ];

    public static bool IsAdmin()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static string OwnJob()
    {
        Native.IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out var inJob);
        if (!inJob)
        {
            return "not in a job";
        }

        var flags = Native.CurrentJobLimitFlags();
        return $"in a job, limit flags 0x{flags:x} (breakaway ok {(flags & 0x800) != 0}, silent breakaway {(flags & 0x1000) != 0}, kill on close {(flags & 0x2000) != 0})";
    }

    public static async Task<bool> RunOnce(string browserPath, int iteration, bool verbose)
    {
        var clock = Stopwatch.StartNew();
        var lines = new List<string>();
        void Log(string s) => lines.Add($"[{iteration}] {clock.Elapsed.TotalMilliseconds,7:F0} ms  {s}");

        var root = Path.Combine(Path.GetTempPath(), "hc360-edge-diag");
        var name = $"{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var profile = Path.Combine(root, name);
        Directory.CreateDirectory(profile);

        var toBrowser = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromBrowser = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var job = Native.CreateKillOnCloseJob();
        var start = new ProcessStartInfo(browserPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Switches)
        {
            start.ArgumentList.Add(a);
        }

        start.ArgumentList.Add($"--user-data-dir={profile}");
        start.ArgumentList.Add("--remote-debugging-pipe");
        start.ArgumentList.Add($"--remote-debugging-io-pipes={toBrowser.GetClientHandleAsString()},{fromBrowser.GetClientHandleAsString()}");
        start.ArgumentList.Add("about:blank");

        var t0 = clock.Elapsed;
        var process = Process.Start(start)!;
        var t1 = clock.Elapsed;
        var assigned = Native.AssignProcessToJobObject(job, process.SafeHandle);
        var error = assigned ? 0 : Marshal.GetLastPInvokeError();
        var t2 = clock.Elapsed;
        var assignedAt = DateTime.Now;
        var early = Native.Descendants(process.Id);
        Log($"main pid {process.Id}: Start {(t1 - t0).TotalMilliseconds:F1} ms, Assign {(t2 - t1).TotalMilliseconds:F2} ms later at {assignedAt:HHmmss.ffffff} (ok {assigned}, error {error}); descendants then: {early.Count}");

        var exitedAt = TimeSpan.Zero;
        var outEof = TimeSpan.Zero;
        var errEof = TimeSpan.Zero;
        var stderr = new ConcurrentQueue<string>();
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => exitedAt = clock.Elapsed;
        process.OutputDataReceived += (_, l) => { if (l.Data is null) { outEof = clock.Elapsed; } };
        process.ErrorDataReceived += (_, l) =>
        {
            if (l.Data is null)
            {
                errEof = clock.Elapsed;
            }
            else if (!string.IsNullOrWhiteSpace(l.Data))
            {
                stderr.Enqueue(l.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        toBrowser.DisposeLocalCopyOfClientHandle();
        fromBrowser.DisposeLocalCopyOfClientHandle();
        var cdp = new Cdp(toBrowser, fromBrowser);

        var ok = true;
        try
        {
            var version = await cdp.Send("Browser.getVersion");
            Log($"getVersion: {version.GetProperty("product").GetString()}");
            Report("after getVersion", name, job, process.Id, assignedAt, Log);

            var context = (await cdp.Send("Target.createBrowserContext", new JsonObject { ["disposeOnDetach"] = true })).GetProperty("browserContextId").GetString()!;
            var target = (await cdp.Send("Target.createTarget", new JsonObject { ["url"] = "about:blank", ["browserContextId"] = context })).GetProperty("targetId").GetString()!;
            var session = (await cdp.Send("Target.attachToTarget", new JsonObject { ["targetId"] = target, ["flatten"] = true })).GetProperty("sessionId").GetString()!;
            await cdp.Send("Emulation.setScriptExecutionDisabled", new JsonObject { ["value"] = true }, session);
            await cdp.Send("Network.enable", null, session);
            await cdp.Send("Page.enable", null, session);
            var frame = (await cdp.Send("Page.getFrameTree", null, session)).GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cdp.OnEvent = (method, s, _) => { if (s == session && method == "Page.loadEventFired") { loaded.TrySetResult(); } };
            await cdp.Send("Page.setDocumentContent", new JsonObject { ["frameId"] = frame, ["html"] = "<!doctype html><h1>Diag</h1><p>x</p>" }, session);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var pdf = await cdp.Send("Page.printToPDF", new JsonObject { ["printBackground"] = true }, session, 60000);
            Log($"printed {pdf.GetProperty("data").GetString()!.Length} base64 chars");
            Report("after print", name, job, process.Id, assignedAt, Log);
            Log("profile top level: " + string.Join(", ", Directory.EnumerateFileSystemEntries(profile).Select(Path.GetFileName)));
        }
        catch (Exception ex)
        {
            Log($"render failed: {ex.GetType().Name} {ex.Message}");
        }

        // Close exactly as HeadlessBrowser.DisposeAsync does.
        var closeStarted = clock.Elapsed;
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await cdp.Send("Browser.close", timeoutMs: 5000);
                Log("Browser.close answered");
            }
            catch (IOException)
            {
                Log("Browser.close: pipe closed first");
            }

            await process.WaitForExitAsync(wait.Token);
            Log($"WaitForExitAsync returned after {(clock.Elapsed - closeStarted).TotalMilliseconds:F0} ms");
        }
        catch (Exception ex)
        {
            Log($"close wait: {ex.GetType().Name} after {(clock.Elapsed - closeStarted).TotalMilliseconds:F0} ms");
            ok = false;
        }

        Log($"main exited at {Ms(exitedAt)}, stdout EOF at {Ms(outEof)}, stderr EOF at {Ms(errEof)} (close sent at {Ms(closeStarted)})");
        toBrowser.Dispose();
        fromBrowser.Dispose();
        bool hasExited;
        try
        {
            hasExited = process.HasExited;
        }
        catch (InvalidOperationException)
        {
            hasExited = true;
        }

        if (!hasExited)
        {
            Report("main still running at the end of the wait", name, job, process.Id, assignedAt, Log);
            try
            {
                process.Kill(entireProcessTree: true);
                Log("Kill(entireProcessTree) called");
            }
            catch (Exception ex)
            {
                Log($"Kill: {ex.GetType().Name} {ex.Message}");
            }
        }

        Native.TerminateJobObject(job, 1);
        var waitStart = clock.Elapsed;
        while (Native.ActiveProcesses(job) > 0 && (clock.Elapsed - waitStart).TotalSeconds < 10)
        {
            await Task.Delay(50);
        }

        Log($"job empty after {(clock.Elapsed - waitStart).TotalMilliseconds:F0} ms (active {Native.ActiveProcesses(job)})");
        var left = ProfileProcesses(name);
        if (left.Count > 0)
        {
            ok = false;
            Report("still running after the job ended", name, job, process.Id, assignedAt, Log);
        }

        var deleted = false;
        for (var attempt = 0; attempt < 5 && !deleted; attempt++)
        {
            try
            {
                Directory.Delete(profile, recursive: true);
                deleted = true;
                Log($"profile deleted on attempt {attempt + 1}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"delete attempt {attempt + 1}: {ex.Message}");
                await Task.Delay(200 * (attempt + 1));
            }
        }

        if (!deleted)
        {
            ok = false;
            Report("profile still locked", name, job, process.Id, assignedAt, Log);
            foreach (var p in Native.Lockers(profile))
            {
                Log($"  locker: pid {p.Pid} {p.App} {CommandLineOf(p.Pid)}");
            }
        }

        if (!ok || verbose)
        {
            foreach (var line in stderr.Take(30))
            {
                Log("  edge stderr: " + line);
            }
        }

        // Clean up only the processes that carry this profile.
        foreach (var p in ProfileProcesses(name))
        {
            try
            {
                using var leftover = Process.GetProcessById(p.Pid);
                leftover.Kill();
                Log($"killed leftover {p.Pid}");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        Native.CloseHandle(job);
        if (!deleted)
        {
            await Task.Delay(1000);
            try
            {
                Directory.Delete(profile, recursive: true);
                Log("deleted after killing the leftovers");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"still not deleted after killing the leftovers: {ex.Message}");
            }
        }

        Log(ok ? "OK" : "PROBLEM");
        foreach (var line in ok && !verbose ? lines.Where(l => l.Contains("main pid", StringComparison.Ordinal) || l.Contains("main exited", StringComparison.Ordinal) || l.EndsWith("OK", StringComparison.Ordinal)) : lines)
        {
            Console.WriteLine(line);
        }

        return ok;
    }

    private static string Ms(TimeSpan t) => t == TimeSpan.Zero ? "never" : $"{t.TotalMilliseconds:F0} ms";

    private static void Report(string label, string name, IntPtr job, int mainPid, DateTime assignedAt, Action<string> log)
    {
        var list = ProfileProcesses(name);
        log($"{label}: {list.Count} processes carry the profile; job active {Native.ActiveProcesses(job)}");
        foreach (var pid in Native.JobProcessIds(job).Where(id => list.All(p => p.Pid != id)))
        {
            log($"  in the job without the profile: pid {pid} {Diag.CommandLineOf(pid)}");
        }
        foreach (var p in list)
        {
            var handle = Native.OpenProcess(0x1000, false, p.Pid);
            var inJob = false;
            var inAny = false;
            if (handle != IntPtr.Zero)
            {
                Native.IsProcessInJob(handle, job, out inJob);
                Native.IsProcessInJob(handle, IntPtr.Zero, out inAny);
                Native.CloseHandle(handle);
            }

            var beforeAssign = p.Created < assignedAt ? " CREATED BEFORE ASSIGN" : "";
            log($"  pid {p.Pid,6} ppid {p.Parent,6}{(p.Pid == mainPid ? " (main)" : "")} {p.Name} type={p.Type} inJob={inJob} inAnyJob={inAny} created={p.Created:HHmmss.ffffff}{beforeAssign}");
            if ((p.Type == "browser" && p.Pid != mainPid) || !inJob || label.Contains("still", StringComparison.Ordinal))
            {
                log($"    command line: {CommandLineOf(p.Pid)}");
            }
        }
    }

    public sealed record EdgeProcess(int Pid, int Parent, string Name, string Type, DateTime Created);

    public static string CommandLineOf(int pid)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
        foreach (var item in searcher.Get())
        {
            using var o = (ManagementObject)item;
            return o["CommandLine"] as string ?? "";
        }

        return "(gone)";
    }

    public static List<EdgeProcess> ProfileProcesses(string name)
    {
        var result = new List<EdgeProcess>();
        using var searcher = new ManagementObjectSearcher($"SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process WHERE CommandLine LIKE '%{name}%'");
        foreach (var item in searcher.Get())
        {
            using var o = (ManagementObject)item;
            var commandLine = o["CommandLine"] as string ?? "";
            if (commandLine.Contains("Win32_Process", StringComparison.Ordinal))
            {
                continue;
            }

            var type = "browser";
            var at = commandLine.IndexOf("--type=", StringComparison.Ordinal);
            if (at >= 0)
            {
                var end = commandLine.IndexOf(' ', at);
                type = commandLine[(at + 7)..(end < 0 ? commandLine.Length : end)];
                var sub = commandLine.IndexOf("--utility-sub-type=", StringComparison.Ordinal);
                if (sub >= 0)
                {
                    var subEnd = commandLine.IndexOf(' ', sub);
                    type += "/" + commandLine[(sub + 19)..(subEnd < 0 ? commandLine.Length : subEnd)];
                }
            }

            var created = o["CreationDate"] is string c ? ManagementDateTimeConverter.ToDateTime(c) : DateTime.MinValue;
            result.Add(new EdgeProcess(Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture), Convert.ToInt32(o["ParentProcessId"], CultureInfo.InvariantCulture), (string)o["Name"], type, created));
        }

        return result;
    }
}

sealed class Cdp
{
    private readonly Stream _out;
    private readonly Stream _in;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private int _id;

    public Cdp(Stream output, Stream input)
    {
        _out = output;
        _in = input;
        new Thread(Read) { IsBackground = true }.Start();
    }

    public Action<string, string?, JsonElement>? OnEvent { get; set; }

    public async Task<JsonElement> Send(string method, JsonObject? parameters = null, string? session = null, int timeoutMs = 60000)
    {
        var id = Interlocked.Increment(ref _id);
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
        if (session is not null)
        {
            message["sessionId"] = session;
        }

        lock (_out)
        {
            _out.Write(Encoding.UTF8.GetBytes(message.ToJsonString()));
            _out.WriteByte(0);
            _out.Flush();
        }

        return await reply.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    private void Read()
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (true)
            {
                var read = _in.Read(buffer);
                if (read == 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != 0)
                    {
                        message.WriteByte(buffer[i]);
                        continue;
                    }

                    using var doc = JsonDocument.Parse(message.ToArray());
                    message.SetLength(0);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var id) && _pending.TryRemove(id.GetInt32(), out var reply))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            reply.TrySetException(new InvalidOperationException(error.GetRawText()));
                        }
                        else
                        {
                            reply.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default);
                        }
                    }
                    else if (root.TryGetProperty("method", out var method))
                    {
                        OnEvent?.Invoke(method.GetString()!, root.TryGetProperty("sessionId", out var s) ? s.GetString() : null, root.TryGetProperty("params", out var p) ? p.Clone() : default);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException)
        {
        }

        foreach (var reply in _pending.Values)
        {
            reply.TrySetException(new IOException("pipe closed"));
        }
    }
}

static class Native
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr sa, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int cls, ref JobExtended info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool AssignProcessToJobObject(IntPtr job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int cls, out JobAccounting info, uint length, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int cls, out JobExtended info, uint length, IntPtr returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateJobObject(IntPtr job, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint apps, IntPtr unique, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, ref uint reasons);

    public static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new JobExtended { Basic = { LimitFlags = 0x2000 } };
        SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<JobExtended>());
        return job;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int cls, [Out] UIntPtr[] list, uint length, IntPtr returned);

    public static List<int> JobProcessIds(IntPtr job)
    {
        var buffer = new UIntPtr[2 + 256];
        if (!QueryInformationJobObject(job, 3, buffer, (uint)(buffer.Length * UIntPtr.Size), IntPtr.Zero))
        {
            return [];
        }

        var count = (int)(buffer[0].ToUInt64() >> 32);
        var listed = (int)(buffer[0].ToUInt64() & 0xffffffff);
        return [.. buffer.Skip(1).Take(listed).Select(p => (int)p.ToUInt64())];
    }

    public static uint CurrentJobLimitFlags() =>
        QueryInformationJobObject(IntPtr.Zero, 9, out JobExtended info, (uint)Marshal.SizeOf<JobExtended>(), IntPtr.Zero) ? info.Basic.LimitFlags : 0xffffffff;

    public static uint ActiveProcesses(IntPtr job) =>
        QueryInformationJobObject(job, 1, out JobAccounting info, (uint)Marshal.SizeOf<JobAccounting>(), IntPtr.Zero) ? info.ActiveProcesses : 9999;

    public static List<int> Descendants(int root)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        if (Process32FirstW(snapshot, ref entry))
        {
            do
            {
                parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
            while (Process32NextW(snapshot, ref entry));
        }

        CloseHandle(snapshot);
        var result = new List<int>();
        var frontier = new Queue<int>([root]);
        while (frontier.TryDequeue(out var parent))
        {
            foreach (var (child, p) in parents)
            {
                if (p == parent && child != root && !result.Contains(child))
                {
                    result.Add(child);
                    frontier.Enqueue(child);
                }
            }
        }

        return result;
    }

    public static List<(int Pid, string App)> Lockers(string folder)
    {
        var result = new List<(int, string)>();
        string[] files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(3000).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        if (files.Length == 0 || RmStartSession(out var session, 0, new StringBuilder(64)) != 0)
        {
            return result;
        }

        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0)
            {
                return result;
            }

            uint count = 0;
            uint reasons = 0;
            var rc = RmGetList(session, out var needed, ref count, null, ref reasons);
            if (rc == 234 && needed > 0)
            {
                var info = new RmProcessInfo[needed];
                count = needed;
                RmGetList(session, out needed, ref count, info, ref reasons);
                result.AddRange(info.Take((int)count).Select(i => (i.Process.ProcessId, i.AppName)));
            }
        }
        finally
        {
            RmEndSession(session);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasic
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
    private struct JobExtended
    {
        public JobBasic Basic;
        public ulong R1, R2, R3, R4, R5, R6;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobAccounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public uint StartTimeLow;
        public uint StartTimeHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}
