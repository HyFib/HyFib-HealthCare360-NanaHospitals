#pragma warning disable
// TEMPORARY, diagnostics branch only: records what happens when the PDF browser closes on the CI runner.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Pdf;

internal static class EdgeDiag
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<string>> Notes = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object FileLock = new();

    public static string ReportPath => Path.Combine(Path.GetTempPath(), "hc360-edge-diag-report.txt");

    public static void Note(string profile, string text) =>
        Notes.GetOrAdd(profile, _ => new()).Enqueue($"{DateTime.Now:HH:mm:ss.fff} (+{Clock.ElapsedMilliseconds} ms) {text}");

    public static void Snapshot(string profile, string label, Func<int, bool>? inJob, int mainPid, DateTime assignedAt)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var name = Path.GetFileName(profile);
            using var searcher = new ManagementObjectSearcher($"SELECT ProcessId, ParentProcessId, Name, CommandLine, CreationDate FROM Win32_Process WHERE CommandLine LIKE '%{name}%'");
            var lines = new List<string>();
            foreach (var item in searcher.Get())
            {
                using var o = (ManagementObject)item;
                var commandLine = o["CommandLine"] as string ?? "";
                if (commandLine.Contains("Win32_Process", StringComparison.Ordinal))
                {
                    continue;
                }

                var pid = Convert.ToInt32(o["ProcessId"], CultureInfo.InvariantCulture);
                var type = "browser";
                var at = commandLine.IndexOf("--type=", StringComparison.Ordinal);
                if (at >= 0)
                {
                    var end = commandLine.IndexOf(' ', at);
                    type = commandLine[(at + 7)..(end < 0 ? commandLine.Length : end)];
                }

                var created = o["CreationDate"] is string c ? ManagementDateTimeConverter.ToDateTime(c) : DateTime.MinValue;
                var job = inJob?.Invoke(pid) ?? false;
                Native.IsProcessInJob(pid, out var anyJob);
                var line = $"  pid {pid} ppid {o["ParentProcessId"]}{(pid == mainPid ? " (main)" : "")} type={type} inJob={job} inAnyJob={anyJob} created={created:HH:mm:ss.ffffff}{(created < assignedAt ? " BEFORE ASSIGN" : "")}";
                if (!job || type == "browser")
                {
                    line += $"\n      {commandLine}";
                }

                lines.Add(line);
            }

            Note(profile, $"{label}: {lines.Count} processes carry the profile");
            foreach (var line in lines)
            {
                Note(profile, line);
            }
        }
        catch (Exception ex)
        {
            Note(profile, $"{label}: snapshot failed: {ex.Message}");
        }
    }

    public static void Lockers(string profile)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var lockers = Native.Lockers(profile);
            Note(profile, $"{lockers.Count} processes hold files in the profile");
            foreach (var (pid, app) in lockers)
            {
                using var searcher = new ManagementObjectSearcher($"SELECT ParentProcessId, CommandLine, CreationDate FROM Win32_Process WHERE ProcessId = {pid}");
                var details = "(gone)";
                foreach (var item in searcher.Get())
                {
                    using var o = (ManagementObject)item;
                    Native.IsProcessInJob(pid, out var anyJob);
                    details = $"ppid {o["ParentProcessId"]} inAnyJob={anyJob} created={(o["CreationDate"] is string c ? ManagementDateTimeConverter.ToDateTime(c).ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture) : "?")}\n      {o["CommandLine"]}";
                }

                Note(profile, $"  locker: pid {pid} {app} {details}");
            }
        }
        catch (Exception ex)
        {
            Note(profile, $"lockers failed: {ex.Message}");
        }
    }

    public static void Flush(string profile, bool problem)
    {
        if (Notes.TryRemove(profile, out var lines) && problem)
        {
            lock (FileLock)
            {
                File.AppendAllLines(ReportPath, [$"==== {profile}", .. lines, ""]);
            }
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint session);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint session, uint files, string[] names, uint apps, IntPtr unique, uint services, string[]? serviceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, ref uint reasons);

        public static void IsProcessInJob(int pid, out bool inAnyJob)
        {
            inAnyJob = false;
            var handle = OpenProcess(0x1000, false, pid);
            if (handle != IntPtr.Zero)
            {
                IsProcessInJob(handle, IntPtr.Zero, out inAnyJob);
                CloseHandle(handle);
            }
        }

        public static List<(int Pid, string App)> Lockers(string folder)
        {
            var result = new List<(int, string)>();
            var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(3000).ToArray();
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
                if (RmGetList(session, out var needed, ref count, null, ref reasons) == 234 && needed > 0)
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
}
