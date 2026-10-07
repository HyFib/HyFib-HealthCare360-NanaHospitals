using System.Diagnostics;
using System.Globalization;
using System.Text;
using HyFib.HealthCare360.WindowsInterop;

namespace HyFib.HealthCare360.Integrations.Tests.Pdf;

/// <summary>
/// The job object the PDF browser runs in, tried with PowerShell, which every Windows machine has. Job objects are a
/// Windows feature; elsewhere these tests are skipped.
/// </summary>
public sealed class ProcessJobTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-job-tests", Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task Ending_the_job_waits_until_its_processes_have_closed_their_files()
    {
        using var job = Job();

        // With many files open, a process takes a while to finish exiting once it is ended, as the browser does.
        using var program = job.Start(PowerShell, Script(
            $"$files = foreach ($i in 1..1000) {{ [IO.File]::Open((Join-Path '{_folder}' \"$i.dat\"), 'OpenOrCreate', 'ReadWrite', 'None') }}",
            "$files.Count"));
        (await ReadyAsync()).ShouldBe("1000");

        (await job.TerminateAndWaitAsync(TimeSpan.FromSeconds(60))).ShouldBeTrue();

        program.HasExited.ShouldBeTrue();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Closing_the_job_ends_the_program_and_the_processes_it_started()
    {
        using var job = Job();
        using var program = job.Start(PowerShell, Script(
            $"$start = New-Object Diagnostics.ProcessStartInfo('{PowerShell}', '-NoProfile -NonInteractive -Command Start-Sleep -Seconds 600'); "
                + "$start.UseShellExecute = $false; $start.CreateNoWindow = $true; $child = [Diagnostics.Process]::Start($start)",
            "$child.Id"));
        using var child = Process.GetProcessById(int.Parse(await ReadyAsync(), CultureInfo.InvariantCulture));
        _ = child.SafeHandle;
        child.HasExited.ShouldBeFalse();

        job.Dispose();

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        await program.WaitForExitAsync(wait.Token);
        await child.WaitForExitAsync(wait.Token);
    }

    private static ProcessJob Job()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job objects are a Windows feature.");
        return ProcessJob.TryCreateKillOnClose().ShouldNotBeNull();
    }

    // Runs the script, writes what the last expression gives to a file named "ready", then waits to be ended. The script
    // goes in encoded, so no quoting can change it on the way.
    private string[] Script(string body, string ready)
    {
        Directory.CreateDirectory(_folder);
        var script = $"{body}; [IO.File]::WriteAllText((Join-Path '{_folder}' 'ready.tmp'), {ready}); "
            + $"[IO.File]::Move((Join-Path '{_folder}' 'ready.tmp'), (Join-Path '{_folder}' 'ready')); Start-Sleep -Seconds 600";
        return ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];
    }

    private async Task<string> ReadyAsync()
    {
        var ready = Path.Combine(_folder, "ready");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(60));
        while (!File.Exists(ready))
        {
            await Task.Delay(50, wait.Token);
        }

        return await File.ReadAllTextAsync(ready, wait.Token);
    }
}
