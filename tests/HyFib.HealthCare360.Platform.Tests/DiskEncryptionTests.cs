using System.Runtime.Versioning;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Platform.Tests;

public sealed class DiskEncryptionTests : IDisposable
{
    private static readonly DateTimeOffset Checked = new(2026, 10, 7, 0, 45, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-disk-encryption-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Disks_that_BitLocker_protects_raise_no_warning()
    {
        var report = Report(On("C:"), On("D:"));

        DiskEncryption.Warnings(report, Checked.AddHours(20)).ShouldBeEmpty();
        DiskEncryption.AllProtected(report).ShouldBeTrue();
        DiskEncryption.Describe(report).ShouldBe(["C: BitLocker on", "D: BitLocker on"]);
    }

    [Fact]
    public void A_disk_that_is_not_encrypted_is_critical()
    {
        var report = Report(On("C:"), new("D:", VolumeProtection.Off, VolumeConversion.NotEncrypted), new("E:", VolumeProtection.Off, VolumeConversion.Decrypting));

        var warning = DiskEncryption.Warnings(report, Checked).ShouldHaveSingleItem();

        warning.Code.ShouldBe("disk.unencrypted");
        warning.Severity.ShouldBe("critical");
        warning.Message.ShouldStartWith("BitLocker is off on D: and E:, so patient data there is not encrypted.");
        DiskEncryption.AllProtected(report).ShouldBeFalse();
    }

    [Fact]
    public void Suspended_unfinished_and_unknown_protection_are_warnings()
    {
        var report = Report(
            new("C:", VolumeProtection.Off, VolumeConversion.Encrypted),
            new("D:", VolumeProtection.Off, VolumeConversion.Encrypting),
            new("E:", VolumeProtection.Unknown, VolumeConversion.Unknown));

        var warnings = DiskEncryption.Warnings(report, Checked);

        warnings.Select(w => (w.Code, w.Severity)).ShouldBe(
        [
            ("disk.encrypting", "warning"),
            ("disk.encryption_suspended", "warning"),
            ("disk.encryption_unknown", "warning"),
        ]);
        warnings[1].Message.ShouldContain("suspended on C:");
        DiskEncryption.Describe(report).ShouldBe(
        [
            "C: BitLocker suspended (encrypted, key readable on the disk)",
            "D: BitLocker still encrypting",
            "E: BitLocker state unknown (the disk may be locked)",
        ]);
    }

    [Fact]
    public void A_machine_without_BitLocker_is_critical()
    {
        var report = new DiskEncryptionReport(Checked, Available: false, []);

        DiskEncryption.Warnings(report, Checked).ShouldHaveSingleItem().Code.ShouldBe("disk.unencrypted");
        DiskEncryption.AllProtected(report).ShouldBeFalse();
        DiskEncryption.Describe(report).ShouldBe(["BitLocker is not installed on this machine."]);
    }

    [Fact]
    public void A_check_that_never_ran_or_stopped_running_is_reported()
    {
        DiskEncryption.Warnings(null, Checked).ShouldHaveSingleItem().Code.ShouldBe("disk.encryption_unchecked");

        var stale = DiskEncryption.Warnings(Report(On("C:")), Checked + DiskEncryption.MaxAge + TimeSpan.FromMinutes(1)).ShouldHaveSingleItem();
        stale.Code.ShouldBe("disk.encryption_stale");
        stale.Message.ShouldContain("07 Oct 2026");
        stale.Message.ShouldContain(DiskEncryption.ScheduledTaskName);
    }

    [Fact]
    public void The_record_survives_a_round_trip_and_a_damaged_one_reads_as_missing()
    {
        var path = Path.Combine(_root, "config", DiskEncryption.FileName);
        var report = Report(On("C:"), new("D:", VolumeProtection.Off, VolumeConversion.NotEncrypted));

        DiskEncryption.Save(path, report);
        var loaded = DiskEncryption.Load(path).ShouldNotBeNull();

        loaded.CheckedAt.ShouldBe(report.CheckedAt);
        loaded.Available.ShouldBeTrue();
        loaded.Volumes.ShouldBe(report.Volumes);
        File.ReadAllText(path).ShouldContain("\"NotEncrypted\"", Case.Sensitive, "enums are written by name");

        File.WriteAllText(path, "{ not json");
        DiskEncryption.Load(path).ShouldBeNull();
        DiskEncryption.Load(Path.Combine(_root, "missing.json")).ShouldBeNull();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_check_reads_BitLocker_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "BitLocker exists only on Windows; the Windows CI job runs this.");

        // The CI machine may or may not have BitLocker, and may run without administrator rights: each answer is fine, as
        // long as the check answers.
        try
        {
            var report = DiskEncryption.Check(DateTimeOffset.UtcNow);
            report.Volumes.ShouldAllBe(v => v.Drive.Length == 2 && v.Drive[1] == ':');
        }
        catch (UnauthorizedAccessException ex)
        {
            ex.Message.ShouldContain("administrator rights");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static DiskEncryptionReport Report(params VolumeEncryption[] volumes) => new(Checked, Available: true, volumes);

    private static VolumeEncryption On(string drive) => new(drive, VolumeProtection.On, VolumeConversion.Encrypted);
}
