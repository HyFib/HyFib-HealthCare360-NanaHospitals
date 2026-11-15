using HyFib.HealthCare360.Platform.Jobs;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-16, DED-20: a backup is taken at once, not at the next night's, after a failover (the promoted server has none
/// of its own) and after the data encryption key is rotated (a standby or new hardware reads data encrypted with the new
/// key only once a backup has carried it there). One backup tried after the change settles it.
/// </summary>
public sealed class BackupAfterChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_is_due_with_the_original_data_key_and_no_failover()
    {
        BackupAfterChange.Reason(Now.AddHours(-8), dataKeySince: null, promotedAt: null).ShouldBeNull();
        BackupAfterChange.Reason(null, dataKeySince: null, promotedAt: null).ShouldBeNull("a new installation waits for its nightly schedule");
    }

    [Fact]
    public void A_backup_is_due_after_a_data_key_rotation_until_one_is_tried()
    {
        BackupAfterChange.Reason(Now.AddHours(-8), dataKeySince: Now.AddMinutes(-5), promotedAt: null).ShouldNotBeNull().ShouldContain("rotated");
        BackupAfterChange.Reason(Now.AddMinutes(-1), dataKeySince: Now.AddMinutes(-5), promotedAt: null).ShouldBeNull("a backup was tried since");
        BackupAfterChange.Reason(null, dataKeySince: Now.AddMinutes(-5), promotedAt: null).ShouldNotBeNull();
    }

    [Fact]
    public void A_backup_is_due_after_a_failover_until_one_is_tried_on_the_promoted_server()
    {
        // The backup history came over with the database: the old primary's last backup is from before the failover.
        BackupAfterChange.Reason(Now.AddHours(-8), dataKeySince: null, promotedAt: Now.AddMinutes(-20)).ShouldNotBeNull().ShouldContain("promoted");
        BackupAfterChange.Reason(Now.AddMinutes(-10), dataKeySince: null, promotedAt: Now.AddMinutes(-20)).ShouldBeNull();
        BackupAfterChange.Reason(Now.AddMinutes(-20), dataKeySince: null, promotedAt: Now.AddMinutes(-20)).ShouldBeNull("tried at the moment of the change");
    }
}
