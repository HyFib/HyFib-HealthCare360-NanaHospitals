using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// Audit P9: the hospital's day is India's wherever HC360 runs. A Windows without the IANA time zone database does not know
/// "Asia/Kolkata"; most modules then counted days in UTC, five and a half hours off.
/// </summary>
public sealed class HospitalTimeZoneTests
{
    [Fact]
    public void Is_India_Standard_Time()
    {
        HospitalTimeZone.Zone.GetUtcOffset(new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc)).ShouldBe(TimeSpan.FromMinutes(330));
        HospitalTimeZone.Zone.GetUtcOffset(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc)).ShouldBe(TimeSpan.FromMinutes(330));
    }

    [Fact]
    public void A_machine_that_does_not_know_the_zone_keeps_Indian_time_not_UTC()
    {
        var zone = HospitalTimeZone.Resolve("Asia/Nowhere");

        zone.BaseUtcOffset.ShouldBe(TimeSpan.FromMinutes(330));
        zone.SupportsDaylightSavingTime.ShouldBeFalse();
    }

    /// <summary>
    /// The nightly backup's 01:30 is the hospital's, not the machine's: a server whose Windows clock is set to UTC (as cloud
    /// machines come) would otherwise back up at 07:00 in India, as the OPD fills.
    /// </summary>
    [Fact]
    public void The_backup_schedule_keeps_Indian_time_where_the_machine_does_not_know_the_zone()
    {
        PlatformServiceCollectionExtensions.ResolveTimeZone("Asia/Nowhere").BaseUtcOffset.ShouldBe(TimeSpan.FromMinutes(330));
    }
}
