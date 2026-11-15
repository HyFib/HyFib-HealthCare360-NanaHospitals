using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// System health on how the backups are set up (DED-16): switched off, which silenced every backup warning, is critical;
/// no second disk or NAS is a warning; and the disks are told apart by the id each is given on its first copy, so where
/// two take turns, one left in place past the weekly swap is flagged, while a NAS, the only disk, never is.
/// </summary>
public sealed class BackupDiskTests : IDisposable
{
    private const string DiskA = "7F3A2C1D";
    private const string DiskB = "B21E9C40";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Nightly_backups_switched_off_are_critical()
    {
        var warning = SystemHealthBackups.SetUpWarnings(new BackupOptions { Enabled = false, SecondaryDirectory = @"E:\HC360-Backups" }, allowsSimulators: false)
            .ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe((SystemHealthBackups.DisabledCode, "critical"));
        warning.Message.ShouldContain("HealthCare360:Backup:Enabled is false");
    }

    [Fact]
    public void No_second_disk_or_NAS_is_a_warning()
    {
        var warning = SystemHealthBackups.SetUpWarnings(new BackupOptions { SecondaryDirectory = null }, allowsSimulators: false).ShouldHaveSingleItem();

        (warning.Code, warning.Severity).ShouldBe((SystemHealthBackups.NoSecondCopyCode, "warning"));
        warning.Message.ShouldContain("-SecondaryBackupDir");
        SystemHealthBackups.SetUpWarnings(new BackupOptions { SecondaryDirectory = string.Empty }, allowsSimulators: false).ShouldHaveSingleItem().Code
            .ShouldBe(SystemHealthBackups.NoSecondCopyCode, "as the installer leaves it without -SecondaryBackupDir");
    }

    [Fact]
    public void Backups_set_up_with_a_second_copy_raise_nothing_and_a_training_server_nothing_at_all()
    {
        SystemHealthBackups.SetUpWarnings(new BackupOptions { SecondaryDirectory = @"\\NAS\HC360-Backups" }, allowsSimulators: false).ShouldBeEmpty();
        SystemHealthBackups.SetUpWarnings(new BackupOptions { Enabled = false }, allowsSimulators: true).ShouldBeEmpty();
    }

    [Fact]
    public void A_disk_is_given_an_id_on_its_first_copy_and_keeps_it()
    {
        var disk = Path.Combine(_root, "usb", "HC360-Backups");
        Directory.CreateDirectory(disk);

        var id = BackupDisk.Identify(disk, Ist(6, 1, 30), NullLogger.Instance).ShouldNotBeNull();

        id.ShouldMatch("^[0-9A-F]{8}$");
        BackupDisk.Identify(disk, Ist(13, 1, 30), NullLogger.Instance).ShouldBe(id);
        File.ReadAllText(Path.Combine(disk, BackupDisk.MarkerFileName)).ShouldContain($"\"diskId\": \"{id}\"");
    }

    /// <summary>Two disks swapped at the same drive letter: each is given its own id, so a swap shows.</summary>
    [Fact]
    public void Two_disks_at_the_same_folder_are_told_apart()
    {
        var first = Path.Combine(_root, "first", "HC360-Backups");
        var second = Path.Combine(_root, "second", "HC360-Backups");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        BackupDisk.Identify(first, Ist(6, 1, 30), NullLogger.Instance).ShouldNotBe(BackupDisk.Identify(second, Ist(7, 1, 30), NullLogger.Instance));
    }

    /// <summary>A marker HC360 did not write, damaged or edited, is left as it is: the copies are recorded without an id.</summary>
    [Fact]
    public void A_marker_with_no_id_HC360_gave_is_left_as_it_is()
    {
        Directory.CreateDirectory(_root);
        var marker = Path.Combine(_root, BackupDisk.MarkerFileName);
        File.WriteAllText(marker, """{ "diskId": "..\\other" }""");

        BackupDisk.Identify(_root, Ist(6, 1, 30), NullLogger.Instance).ShouldBeNull();
        File.ReadAllText(marker).ShouldBe("""{ "diskId": "..\\other" }""");

        File.WriteAllText(marker, "not json");
        BackupDisk.Identify(_root, Ist(6, 1, 30), NullLogger.Instance).ShouldBeNull();
    }

    /// <summary>
    /// Two disks swapped every Friday: disk A took the copies up to Friday the 6th's, disk B every copy since Saturday
    /// the 7th. On Friday the 13th B is due for its swap; once it has taken every copy for more than seven days, on
    /// Saturday morning, System health says so, naming the disk and since when.
    /// </summary>
    [Theory]
    [InlineData(13, 9, 0, false)]
    [InlineData(14, 1, 29, false)]
    [InlineData(14, 9, 0, true)]
    public void A_disk_left_in_place_past_its_swap_is_flagged_from_the_day_after(int day, int hour, int minute, bool flagged)
    {
        var now = Ist(day, hour, minute);
        var copies = Nightly(DiskA, 1, 6).Concat(Nightly(DiskB, 7, now.Day)).Where(c => c.CopiedAt <= now);

        var warning = SystemHealthBackups.DiskWarning(copies, rotationDays: 7, now, HospitalTimeZone.Zone);

        if (!flagged)
        {
            warning.ShouldBeNull();
            return;
        }

        warning.ShouldNotBeNull();
        (warning.Code, warning.Severity).ShouldBe((SystemHealthBackups.DiskNotSwappedCode, "warning"));
        warning.Message.ShouldStartWith($"The backup disk {DiskB} has taken every backup since 07 Nov 2026 01:30, 7 days, longer than the 7 days");
    }

    [Fact]
    public void Disks_swapped_every_week_raise_nothing() =>
        SystemHealthBackups.DiskWarning(Nightly(DiskA, 1, 6).Concat(Nightly(DiskB, 7, 13)).Concat(Nightly(DiskA, 14, 20)), 7, Ist(20, 9, 0), HospitalTimeZone.Zone)
            .ShouldBeNull();

    /// <summary>A NAS share, or a single disk never swapped, is the only disk: there is nothing to swap it for.</summary>
    [Fact]
    public void The_only_disk_is_never_asked_to_be_swapped() =>
        SystemHealthBackups.DiskWarning(Nightly(DiskA, 1, 30), 7, Ist(30, 9, 0), HospitalTimeZone.Zone).ShouldBeNull();

    /// <summary>
    /// The disk taken out and none put in: nothing takes the copies, which the failed copy's own warning says
    /// (<see cref="BackupCopies"/>); and a hospital that does not swap disks sets the period to 0.
    /// </summary>
    [Fact]
    public void Nothing_is_asked_while_no_disk_takes_copies_or_with_no_rotation_period()
    {
        var copies = Nightly(DiskA, 1, 6).Concat(Nightly(DiskB, 7, 16)).ToList();

        SystemHealthBackups.DiskWarning(copies, 7, Ist(19, 9, 0), HospitalTimeZone.Zone).ShouldBeNull("no copy for three days");
        SystemHealthBackups.DiskWarning(copies, 0, Ist(16, 9, 0), HospitalTimeZone.Zone).ShouldBeNull();
        SystemHealthBackups.DiskWarning(copies, 7, Ist(16, 9, 0), HospitalTimeZone.Zone).ShouldNotBeNull();
    }

    /// <summary>A disk's copies recorded on each backup, one per disk: copied again to the same disk, the newer copy counts.</summary>
    [Fact]
    public void A_backup_records_one_copy_per_disk()
    {
        var record = BackupCopies.With(null, new BackupCopy(DiskA, Ist(6, 1, 40)));
        record = BackupCopies.With(record, new BackupCopy(DiskB, Ist(7, 3, 30)));
        record = BackupCopies.With(record, new BackupCopy(DiskA, Ist(13, 3, 30)));

        BackupCopies.Read(record).ShouldBe([new BackupCopy(DiskB, Ist(7, 3, 30)), new BackupCopy(DiskA, Ist(13, 3, 30))]);
        BackupCopies.Read(null).ShouldBeEmpty();
        BackupCopies.Read("not json").ShouldBeEmpty();
    }

    private static IEnumerable<BackupCopy> Nightly(string disk, int firstDay, int lastDay) =>
        Enumerable.Range(firstDay, lastDay - firstDay + 1).Select(day => new BackupCopy(disk, Ist(day, 1, 30)));

    private static DateTimeOffset Ist(int day, int hour, int minute) => new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(5.5));
}
