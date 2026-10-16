using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.Desktop.Tests.Updates;

/// <summary>
/// The release workflow's signing script (tools/release/Sign-HC360Release.ps1) and HC360's own checks agree: what the
/// script signs, with a throwaway key given as the workflow gives its secret, verifies with the code HC360 Desktop, its
/// updater and the server run. tests/deploy/Test-ReleaseSigning.ps1 checks the script's other behaviour.
/// </summary>
public sealed class SigningScriptTests
{
    private const string KeyVariable = "HC360_RELEASE_SIGNING_KEY";

    [Fact]
    public void What_the_release_script_signs_verifies_with_HC360s_checks()
    {
        using var folder = new TempFolder();
        using var key = new TestSigningKey();
        var (package, version) = Package(folder.Path);

        var run = RunScript(folder, key.PrivatePem);

        run.ExitCode.ShouldBe(0, run.Output);
        var manifest = ReleasePackages.ReadManifest(
            File.ReadAllBytes(ReleasePackages.ManifestPathFor(package)), File.ReadAllBytes(ReleasePackages.SignaturePathFor(package)),
            [key.Public], ReleasePackages.DesktopProduct);
        using (var zip = File.OpenRead(package))
        {
            ReleasePackages.CheckPackage(zip, manifest);
        }

        manifest.Version.ShouldBe(version);
        manifest.File.ShouldBe(Path.GetFileName(package));
        var numbers = version.Split('.');
        manifest.MinimumServerVersion.ShouldBe($"{numbers[0]}.{numbers[1]}.0");
        File.ReadAllText(folder.Combine("outputs.txt")).ShouldContain("signed=true");
        File.ReadAllText(folder.Combine("summary.md")).ShouldContain(key.Public.Id);

        // The key appears in no output.
        var keyBody = key.PrivatePem.Split('\n')[1].Trim();
        run.Output.ShouldNotContain(keyBody);
        File.ReadAllText(folder.Combine("summary.md")).ShouldNotContain(keyBody);
    }

    [Fact]
    public void Without_the_key_the_release_is_built_but_says_plainly_that_it_is_not_signed()
    {
        using var folder = new TempFolder();
        var (package, _) = Package(folder.Path);

        var run = RunScript(folder, privatePem: null);

        run.ExitCode.ShouldBe(0, run.Output);
        File.Exists(ReleasePackages.ManifestPathFor(package)).ShouldBeFalse();
        File.Exists(ReleasePackages.SignaturePathFor(package)).ShouldBeFalse();
        File.ReadAllText(folder.Combine("summary.md")).ShouldContain("NOT signed");
        File.ReadAllText(folder.Combine("outputs.txt")).ShouldContain("signed=false");
    }

    // A package like the release's: its HC360Desktop.exe is a copy of a Windows program, so it has a real product
    // version, which the script checks against the package's name. The copy's version is read, not the original's: in
    // System32 Windows reads it from the program's language resources, which the copy does not have.
    private static (string Path, string Version) Package(string directory)
    {
        var program = Path.Combine(directory, DesktopUpdateLayout.DesktopExe);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), program);
        var version = FileVersionInfo.GetVersionInfo(program).ProductVersion!.Split('+')[0];
        var package = Path.Combine(directory, "artifacts", $"HC360-Desktop-{version}-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(package)!);
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(program, DesktopUpdateLayout.DesktopExe);
        }

        return (package, version);
    }

    private static (int ExitCode, string Output) RunScript(TempFolder folder, string? privatePem)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RepositoryRoot(), "tools", "release", "Sign-HC360Release.ps1"),
                     "-ArtifactsDir", folder.Combine("artifacts"), "-SummaryPath", folder.Combine("summary.md"), "-OutputPath", folder.Combine("outputs.txt"),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove(KeyVariable);
        start.Environment.Remove("GITHUB_ACTIONS");
        if (privatePem is not null)
        {
            start.Environment[KeyVariable] = privatePem;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        process.WaitForExit(TimeSpan.FromMinutes(2)).ShouldBeTrue("The signing script did not finish within 2 minutes.");
        return (process.ExitCode, output.Result + error.Result);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test's folder.");
    }
}
