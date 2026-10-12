using System.IO;
using HyFib.HealthCare360.DesktopUpdater;
using HyFib.HealthCare360.WindowsInterop.Updates;

namespace HyFib.HealthCare360.Desktop.Tests.Updates;

/// <summary>
/// The updater installs a staged package only after verifying it itself, swaps it in while HC360 Desktop is closed, and
/// puts the previous version back when that fails (SRS DED-18). In temp folders: a program's "version" is its text.
/// </summary>
public sealed class DesktopInstallerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly TestSigningKey _key = new();
    private bool _running;
    private bool _allowed = true;
    private Action<string> _step = _ => { };

    public DesktopInstallerTests()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Path.Combine(Install, DesktopUpdateLayout.DesktopExe), "1.1.0");
        File.WriteAllText(Path.Combine(Install, "settings-the-old-version-had.txt"), "old");
    }

    private string Install => _folder.Combine("HC360 Desktop");

    private string Staging => _folder.Combine("Updates");

    [Fact]
    public void Installs_a_verified_newer_package_and_keeps_the_version_before()
    {
        TestPackage.Create(_key, "1.2.0").WriteStaged(Staging);

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Installed, status.Message);
        status.Version.ShouldBe("1.2.0");
        File.ReadAllText(Path.Combine(Install, DesktopUpdateLayout.DesktopExe)).ShouldBe("1.2.0");
        File.Exists(Path.Combine(Install, "runtimes", "win-x64", "native", "WebView2Loader.dll")).ShouldBeTrue();
        File.Exists(Path.Combine(Install, "settings-the-old-version-had.txt")).ShouldBeFalse();
        File.Exists(Path.Combine(Install, "Install-HC360Desktop.ps1")).ShouldBeFalse("the workstation installer is not part of the installed program");
        File.ReadAllText(Path.Combine(DesktopUpdateLayout.PreviousDirectoryFor(Install), DesktopUpdateLayout.DesktopExe)).ShouldBe("1.1.0");
        Directory.Exists(DesktopUpdateLayout.NewDirectoryFor(Install)).ShouldBeFalse();

        // The staging folder is the users': the updater changed nothing there.
        File.Exists(Path.Combine(Staging, DesktopUpdateLayout.PackageFile)).ShouldBeTrue();
    }

    [Fact]
    public void A_package_changed_after_it_was_staged_is_not_installed()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        package.WriteStaged(Staging);
        var swapped = TestPackage.Create(_key, "1.2.0", addEntries: archive => TestPackage.Add(archive, "planted.dll", "not HyFib's"));
        File.WriteAllBytes(Path.Combine(Staging, DesktopUpdateLayout.PackageFile), swapped.Zip);

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Refused);
        status.Message.ShouldContain("than its manifest says");
        InstalledVersion().ShouldBe("1.1.0");

        // The same size, one byte different.
        var flipped = package.Zip.ToArray();
        flipped[^20] ^= 0x01;
        File.WriteAllBytes(Path.Combine(Staging, DesktopUpdateLayout.PackageFile), flipped);
        status = Apply();
        status.Outcome.ShouldBe(DesktopUpdateOutcome.Refused);
        status.Message.ShouldContain("SHA-256");
        InstalledVersion().ShouldBe("1.1.0");
        Directory.Exists(DesktopUpdateLayout.NewDirectoryFor(Install)).ShouldBeFalse();
    }

    [Fact]
    public void An_unsigned_or_untrusted_package_is_not_installed()
    {
        var package = TestPackage.Create(_key, "1.2.0");
        package.WriteStaged(Staging);
        File.Delete(Path.Combine(Staging, DesktopUpdateLayout.SignatureFile));

        Apply().Outcome.ShouldBe(DesktopUpdateOutcome.Refused);

        using var stranger = new TestSigningKey();
        TestPackage.Create(stranger, "1.2.0").WriteStaged(Staging);
        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Refused);
        status.Message.ShouldContain("does not trust");
        InstalledVersion().ShouldBe("1.1.0");
    }

    [Theory]
    [InlineData("1.1.0")]
    [InlineData("1.0.9")]
    public void A_package_that_is_not_newer_is_left_alone(string version)
    {
        TestPackage.Create(_key, version).WriteStaged(Staging);

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.UpToDate);
        InstalledVersion().ShouldBe("1.1.0");
    }

    [Fact]
    public void Waits_while_HC360_Desktop_runs()
    {
        TestPackage.Create(_key, "1.2.0").WriteStaged(Staging);
        _running = true;

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Waiting);
        InstalledVersion().ShouldBe("1.1.0");

        _running = false;
        Apply().Outcome.ShouldBe(DesktopUpdateOutcome.Installed);
    }

    [Fact]
    public void Puts_the_version_before_back_when_the_new_one_cannot_be_moved_in()
    {
        TestPackage.Create(_key, "1.2.0").WriteStaged(Staging);
        _step = step =>
        {
            if (step == "moved-aside")
            {
                throw new IOException("the disk is full");
            }
        };

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Failed);
        status.Message.ShouldContain("1.1.0 was put back");
        InstalledVersion().ShouldBe("1.1.0");
        File.Exists(Path.Combine(Install, "settings-the-old-version-had.txt")).ShouldBeTrue();
        Directory.Exists(DesktopUpdateLayout.NewDirectoryFor(Install)).ShouldBeFalse();
        Directory.Exists(DesktopUpdateLayout.PreviousDirectoryFor(Install)).ShouldBeFalse();
    }

    [Fact]
    public void Puts_the_version_before_back_when_installing_fails_after_the_new_one_is_in_place()
    {
        TestPackage.Create(_key, "1.2.0").WriteStaged(Staging);
        _step = step =>
        {
            if (step == "installed")
            {
                throw new UnauthorizedAccessException("access denied");
            }
        };

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Failed);
        InstalledVersion().ShouldBe("1.1.0");
    }

    [Fact]
    public void Leaves_the_installed_version_as_it_is_when_HC360_Desktop_starts_during_the_install()
    {
        TestPackage.Create(_key, "1.2.0").WriteStaged(Staging);
        _step = step => _running |= step == "unpacked";

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Waiting);
        InstalledVersion().ShouldBe("1.1.0");
        Directory.Exists(DesktopUpdateLayout.NewDirectoryFor(Install)).ShouldBeFalse();
    }

    [Fact]
    public void A_package_whose_program_is_not_the_version_its_manifest_says_is_not_installed()
    {
        TestPackage.Create(_key, "1.2.0", exeVersion: "1.1.5").WriteStaged(Staging);

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Refused);
        status.Message.ShouldContain("1.1.5");
        InstalledVersion().ShouldBe("1.1.0");
    }

    [Fact]
    public void A_package_with_an_entry_outside_its_folder_is_not_installed()
    {
        TestPackage.Create(_key, "1.2.0", addEntries: archive => TestPackage.Add(archive, "../outside.txt", "escaped")).WriteStaged(Staging);

        var status = Apply();

        status.Outcome.ShouldBe(DesktopUpdateOutcome.Failed);
        File.Exists(_folder.Combine("outside.txt")).ShouldBeFalse();
        InstalledVersion().ShouldBe("1.1.0");
    }

    [Fact]
    public void Does_nothing_when_IT_turned_self_update_off()
    {
        TestPackage.Create(_key, "1.2.0").WriteStaged(Staging);
        _allowed = false;

        Apply().Outcome.ShouldBe(DesktopUpdateOutcome.TurnedOff);
        InstalledVersion().ShouldBe("1.1.0");
    }

    [Fact]
    public void Nothing_staged_is_nothing_to_do()
    {
        Apply().Outcome.ShouldBe(DesktopUpdateOutcome.UpToDate);
    }

    [Fact]
    public void The_policy_turns_self_update_off_only_when_it_says_so()
    {
        var policy = _folder.Combine("policy.json");
        DesktopUpdateLayout.AutoUpdateAllowed(policy).ShouldBeTrue("no policy");
        File.WriteAllText(policy, """{ "serverUrl": "https://hc360-srv:7360/", "kiosk": true }""");
        DesktopUpdateLayout.AutoUpdateAllowed(policy).ShouldBeTrue();
        File.WriteAllText(policy, """{ "autoUpdate": true }""");
        DesktopUpdateLayout.AutoUpdateAllowed(policy).ShouldBeTrue();
        File.WriteAllText(policy, """{ "kiosk": true, "AutoUpdate": false }""");
        DesktopUpdateLayout.AutoUpdateAllowed(policy).ShouldBeFalse();
        File.WriteAllText(policy, """{ "autoUpdate": false }""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        DesktopUpdateLayout.AutoUpdateAllowed(policy).ShouldBeFalse("as Install-HC360Desktop.ps1 -NoAutoUpdate writes it, with a byte order mark");
        File.WriteAllText(policy, "not json");
        DesktopUpdateLayout.AutoUpdateAllowed(policy).ShouldBeTrue("an unreadable policy changes nothing, as for the rest of it");
    }

    [Fact]
    public void The_updater_replaces_itself_with_the_new_versions_copy()
    {
        var updaterDirectory = DesktopUpdateLayout.UpdaterDirectoryFor(Install);
        Directory.CreateDirectory(updaterDirectory);
        var running = Path.Combine(updaterDirectory, DesktopUpdateLayout.UpdaterExe);
        File.WriteAllText(running, "updater 1.1.0");
        File.WriteAllText(Path.Combine(Install, DesktopUpdateLayout.UpdaterExe), "updater 1.2.0");

        SelfUpdate.TryReplace(running, updaterDirectory, Install).ShouldNotBeNull();

        File.ReadAllText(running).ShouldBe("updater 1.2.0");
        File.Exists(running + ".old").ShouldBeTrue();
        SelfUpdate.TryReplace(running, updaterDirectory, Install).ShouldBeNull("it is the same now");
        SelfUpdate.RemoveLeftover(running);
        File.Exists(running + ".old").ShouldBeFalse();

        // Only the copy in the updater's own folder replaces itself.
        SelfUpdate.TryReplace(Path.Combine(Install, DesktopUpdateLayout.UpdaterExe), updaterDirectory, Install).ShouldBeNull();
    }

    [Fact]
    public void Restart_waits_for_the_tasks_record_of_a_run_after_it_started_the_task()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-12T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var earlier = new DesktopUpdateStatus(time.GetUtcNow().AddMinutes(-30), DesktopUpdateOutcome.Waiting, "1.2.0", "waiting");
        var current = earlier;
        var reads = 0;

        var found = RestartFlow.WaitForRun(
            () =>
            {
                if (++reads == 3)
                {
                    current = new DesktopUpdateStatus(time.GetUtcNow(), DesktopUpdateOutcome.Installed, "1.2.0", "installed");
                }

                return current;
            },
            time.GetUtcNow(),
            TimeSpan.FromMinutes(3),
            time,
            time.Advance);

        found.ShouldNotBeNull().Outcome.ShouldBe(DesktopUpdateOutcome.Installed);
        RestartFlow.WaitForRun(() => earlier, time.GetUtcNow(), TimeSpan.FromSeconds(5), time, time.Advance).ShouldBeNull();
    }

    public void Dispose()
    {
        _key.Dispose();
        _folder.Dispose();
    }

    private DesktopUpdateStatus Apply() => new DesktopInstaller(new InstallerContext(
        Install,
        Staging,
        [_key.Public],
        () => _allowed,
        () => _running,
        path => File.Exists(path) ? File.ReadAllText(path) : null,
        TimeProvider.System)
    {
        MoveAttempts = 1,
        MoveRetryDelay = TimeSpan.Zero,
        Step = step => _step(step),
    }).Apply();

    private string InstalledVersion() => File.ReadAllText(Path.Combine(Install, DesktopUpdateLayout.DesktopExe));

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
