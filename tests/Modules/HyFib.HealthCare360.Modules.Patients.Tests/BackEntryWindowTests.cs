using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Tests;

/// <summary>
/// The back-entry windows (bounded, per record type) and a late back-entry approval's reach: one user, one record type,
/// paper dated in its span, so many records, for so long.
/// </summary>
public sealed class BackEntryWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Each_record_type_has_a_window_within_its_bound()
    {
        BackEntryWindows.All.Select(w => w.Record).ShouldBe(Enum.GetValues<BackEntryRecord>());
        BackEntryWindows.All.Where(w => w.Record != BackEntryRecord.Registration).ShouldAllBe(w => w.MaxHours == 14 * 24 && w.DefaultHours <= w.MaxHours);
        BackEntryWindows.For(BackEntryRecord.Registration).ShouldSatisfyAllConditions(
            w => w.DefaultHours.ShouldBe(90 * 24), w => w.MaxHours.ShouldBe(90 * 24));
        BackEntryWindows.All.Select(w => w.SettingKey).ShouldBeUnique();
        BackEntryWindows.SettingKey(BackEntryRecord.ConsultantVisit).ShouldBe("downtime.backEntryHours.consultantVisit");
        BackEntryWindows.SettingKey(BackEntryRecord.PharmacySale).ShouldBe("downtime.backEntryHours.pharmacySale");
    }

    [Theory]
    [InlineData(null, 72)]
    [InlineData(0, 72)]
    [InlineData(1, 1)]
    [InlineData(336, 336)]
    [InlineData(337, 72)]
    [InlineData(-5, 72)]
    public void A_window_outside_its_bound_acts_as_its_default(int? set, int acts)
    {
        BackEntryWindows.For(BackEntryRecord.Visit).Effective(set).ShouldBe(acts);
    }

    [Fact]
    public void A_late_approval_covers_only_its_user_record_span_count_and_time()
    {
        var user = Guid.NewGuid();
        var grant = new LateBackEntryGrant
        {
            Record = BackEntryRecord.Visit,
            RequestedBy = user,
            PaperFrom = Now.AddDays(-6),
            PaperTo = Now.AddDays(-5),
            MaxRecords = 2,
            Used = 1,
            ExpiresAt = Now.AddDays(1),
        };

        grant.Covers(BackEntryRecord.Visit, user, Now.AddDays(-5.5), Now).ShouldBeTrue();
        grant.Covers(BackEntryRecord.Bill, user, Now.AddDays(-5.5), Now).ShouldBeFalse("another record type");
        grant.Covers(BackEntryRecord.Visit, Guid.NewGuid(), Now.AddDays(-5.5), Now).ShouldBeFalse("another user");
        grant.Covers(BackEntryRecord.Visit, user, Now.AddDays(-4), Now).ShouldBeFalse("paper outside the span");
        grant.Covers(BackEntryRecord.Visit, user, Now.AddDays(-5.5), Now.AddDays(2)).ShouldBeFalse("expired");
        grant.Used = 2;
        grant.Covers(BackEntryRecord.Visit, user, Now.AddDays(-5.5), Now).ShouldBeFalse("used up");
    }

    [Fact]
    public void The_pack_prints_restricted_patients_by_uhid_only_unless_the_reader_may_see_them()
    {
        new DowntimePackContext(Guid.NewGuid(), Now, Now.AddHours(24), CanSeeRestricted: false).Patient("Asha Rao", "NH26000001", isVip: true)
            .ShouldBe("Restricted patient (NH26000001)");
        new DowntimePackContext(Guid.NewGuid(), Now, Now.AddHours(24), CanSeeRestricted: true).Patient("Asha Rao", "NH26000001", isVip: true)
            .ShouldBe("Asha Rao (NH26000001)");
        new DowntimePackContext(Guid.NewGuid(), Now, Now.AddHours(24), CanSeeRestricted: false).Patient("Ravi", "NH26000002", isVip: false)
            .ShouldBe("Ravi (NH26000002)");
        DowntimePackOrder.Of("census").ShouldBeLessThan(DowntimePackOrder.Of("mar"));
        DowntimePackOrder.Of("unknown").ShouldBe(DowntimePackOrder.Keys.Count);
    }
}
