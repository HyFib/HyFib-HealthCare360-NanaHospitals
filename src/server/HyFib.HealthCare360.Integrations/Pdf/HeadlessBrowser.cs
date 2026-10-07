using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.WindowsInterop;

namespace HyFib.HealthCare360.Integrations.Pdf;

/// <summary>
/// One headless Edge process with its pipe, a private profile folder and a pool of tabs. A job object ties it to the
/// server, so it never outlives the server even after a crash. Renders lease it; once retired (it failed, sat idle or
/// made enough PDFs) it closes when the last lease ends.
/// </summary>
internal sealed partial class HeadlessBrowser : IAsyncDisposable
{
    // Headless, nothing that phones home, no extensions, no crash uploads. The sandbox stays on.
    private static readonly string[] Switches =
    [
        "--headless",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-component-extensions-with-background-pages",
        "--disable-background-networking",
        "--disable-sync",
        "--disable-default-apps",
        "--disable-breakpad",
        "--disable-domain-reliability",
        "--no-pings",
        "--mute-audio",
        "--disable-gpu",
        "--hide-scrollbars",
    ];

    private readonly Process _process;
    private readonly ProcessJob? _job;
    private readonly DevToolsPipe _pipe;
    private readonly string _profile;
    private readonly ILogger _logger;
    private readonly ConcurrentBag<EdgePdfRenderer.BrowserTab> _idleTabs = [];
    private readonly Lock _state = new();
    private int _leases;
    private bool _retired;
    private int _renders;
    private int _disposed;

    private HeadlessBrowser(Process process, ProcessJob? job, DevToolsPipe pipe, string profile, ILogger logger)
    {
        _process = process;
        _job = job;
        _pipe = pipe;
        _profile = profile;
        _logger = logger;
    }

    /// <summary>The browser's own name and version, for the log and the health report.</summary>
    public string? Product { get; private set; }

    public int Renders => Volatile.Read(ref _renders);

    public static async Task<HeadlessBrowser> StartAsync(string browserPath, string profileRoot, ILogger logger, CancellationToken cancellationToken)
    {
        // The process id in the name lets a later start tell a crashed run's folder from a live one.
        var profile = Path.Combine(profileRoot, $"{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}");
        Directory.CreateDirectory(profile);
        var pipe = new DevToolsPipe();
        var job = ProcessJob.TryCreateKillOnClose();
        var start = new ProcessStartInfo(browserPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in Switches.Append($"--user-data-dir={profile}").Concat(pipe.BrowserArguments).Append("about:blank"))
        {
            start.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{browserPath} did not start.");
        }
        catch
        {
            pipe.Dispose();
            job?.Dispose();
            TryDeleteDirectory(profile);
            throw;
        }

        try
        {
            job?.Assign(process);
        }
        catch (Win32Exception ex)
        {
            logger.LogWarning(ex, "The PDF browser could not be tied to the server process; it is closed on shutdown instead");
        }

        // Edge writes diagnostics to stderr. The pipes must be drained or it blocks once their buffers fill.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, line) =>
        {
            if (!string.IsNullOrWhiteSpace(line.Data))
            {
                logger.LogTrace("PDF browser: {Line}", line.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        pipe.Start();

        var browser = new HeadlessBrowser(process, job, pipe, profile, logger);
        try
        {
            var version = await pipe.SendAsync("Browser.getVersion", null, null, cancellationToken);
            browser.Product = version.GetProperty("product").GetString();
            return browser;
        }
        catch
        {
            await browser.DisposeAsync();
            throw;
        }
    }

    /// <summary>Takes a lease, unless the browser is retired or has stopped.</summary>
    public bool TryLease()
    {
        lock (_state)
        {
            if (_retired || _pipe.Closed.IsCompleted)
            {
                return false;
            }

            _leases++;
            return true;
        }
    }

    /// <summary>Ends a lease. True when the browser is retired and this was its last lease: dispose it now.</summary>
    public bool Release()
    {
        lock (_state)
        {
            _leases--;
            return _retired && _leases == 0;
        }
    }

    /// <summary>No new leases from now on. True when nothing holds a lease (and this call retired it): dispose it now.</summary>
    public bool Retire()
    {
        lock (_state)
        {
            if (_retired)
            {
                return false;
            }

            _retired = true;
            return _leases == 0;
        }
    }

    public async Task<byte[]> PrintAsync(PdfSource source, CancellationToken cancellationToken)
    {
        var tab = _idleTabs.TryTake(out var idle) ? idle : await EdgePdfRenderer.BrowserTab.OpenAsync(_pipe, cancellationToken);
        try
        {
            var pdf = await tab.PrintAsync(source, cancellationToken);
            Interlocked.Increment(ref _renders);
            if (tab.Reusable)
            {
                _idleTabs.Add(tab);
            }
            else
            {
                _ = CloseTabAsync(tab);
            }

            return pdf;
        }
        catch
        {
            _ = CloseTabAsync(tab);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (!_pipe.Closed.IsCompleted)
            {
                // The browser may close the pipe before it answers; either way it is on its way out.
                await _pipe.SendAsync("Browser.close", null, null, wait.Token);
            }

            await _process.WaitForExitAsync(wait.Token);
        }
        catch (Exception ex) when (ex is DevToolsException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "The PDF browser did not close on request");
        }

        _pipe.Dispose();
        if (!HasExited())
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                _logger.LogDebug(ex, "The PDF browser could not be stopped; the job object ends it");
            }
        }

        if (_job is not null)
        {
            // Edge's helper processes outlive the browser for a moment and keep the profile open until they end.
            if (!await _job.TerminateAndWaitAsync(TimeSpan.FromSeconds(10)))
            {
                _logger.LogDebug("Some PDF browser processes were still running 10 seconds after it closed");
            }

            _job.Dispose();
        }

        _process.Dispose();

        if (OperatingSystem.IsWindows())
        {
            await EndLeftoverProcessesAsync();
        }

        // Edge releases its profile files a moment after it exits, longer on a busy machine.
        for (var attempt = 0; attempt < 8 && Directory.Exists(_profile); attempt++)
        {
            if (TryDeleteDirectory(_profile))
            {
                break;
            }

            await Task.Delay(250 * (attempt + 1));
        }

        if (Directory.Exists(_profile))
        {
            _logger.LogWarning("The PDF browser's folder {Folder} could not be deleted; the next start removes it", _profile);
        }
    }

    /// <summary>
    /// Ends the browser's processes that are still running outside its job. Edge can start one before the job takes the
    /// browser in, and such a process keeps the profile open. Every Edge process names the profile folder on its command
    /// line, so they are found by it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task EndLeftoverProcessesAsync()
    {
        foreach (var process in await Task.Run(FindProcessesUsingProfile))
        {
            using (process)
            {
                try
                {
                    _logger.LogWarning("Ending PDF browser process {ProcessId}, which outlived the browser", process.Id);
                    process.Kill(entireProcessTree: true);
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(wait.Token);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or OperationCanceledException)
                {
                    _logger.LogDebug(ex, "PDF browser process {ProcessId} could not be ended", process.Id);
                }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private List<Process> FindProcessesUsingProfile()
    {
        // The folder is named after the server's process id and random hex digits, so it is safe in the query as it is.
        var folder = Path.GetFileName(_profile);
        if (!ProfileFolderName().IsMatch(folder))
        {
            return [];
        }

        var found = new List<Process>();
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE CommandLine LIKE '%{folder}%'");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (var item in searcher.Get())
            {
                using var entry = (ManagementObject)item;
                if (entry["ProcessId"] is uint id && id != Environment.ProcessId)
                {
                    try
                    {
                        found.Add(Process.GetProcessById((int)id));
                    }
                    catch (ArgumentException)
                    {
                        // It ended on its own.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not look for PDF browser processes that outlived the browser");
        }

        return found;
    }

    [GeneratedRegex("^[0-9]+-[0-9a-f]+$")]
    private static partial Regex ProfileFolderName();

    /// <summary>
    /// Removes profile folders left behind by a server that stopped without closing its browser. A folder whose server
    /// process is still running is left alone.
    /// </summary>
    public static void DeleteLeftoverProfiles(string profileRoot, ILogger logger)
    {
        if (!Directory.Exists(profileRoot))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(profileRoot))
        {
            var name = Path.GetFileName(folder);
            var dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && int.TryParse(name.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var processId) && IsRunning(processId))
            {
                continue;
            }

            if (!TryDeleteDirectory(folder))
            {
                logger.LogDebug("Could not delete the old PDF browser folder {Folder}", folder);
            }
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private async Task CloseTabAsync(EdgePdfRenderer.BrowserTab tab)
    {
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tab.CloseAsync(wait.Token);
        }
        catch (Exception ex) when (ex is DevToolsException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "A PDF browser tab did not close");
        }
    }
}
