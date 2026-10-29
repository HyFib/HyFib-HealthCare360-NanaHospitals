using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Privacy.Tests;

public sealed class RetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Rule_keys_are_unique() => RetentionSchedule.Rules.Select(r => r.Key).ShouldBeUnique();

    [Fact]
    public void The_medical_records_periods_are_the_settings_Clinical_reads()
    {
        RetentionSchedule.Get(RetentionSchedule.OutpatientRecords).SettingKey.ShouldBe(RecordRetention.OutpatientYearsSetting);
        RetentionSchedule.Get(RetentionSchedule.InpatientRecords).SettingKey.ShouldBe(RecordRetention.InpatientYearsSetting);
    }

    [Fact]
    public void Only_operational_records_are_deleted_automatically() =>
        RetentionSchedule.Rules.Where(r => r.Action == RetentionAction.Delete).Select(r => r.Key).ShouldBe(
            [RetentionSchedule.Outbox, RetentionSchedule.MessageLog, RetentionSchedule.IntegrationLog, RetentionSchedule.SignInLog, RetentionSchedule.Backups]);

    [Fact]
    public void Medical_records_are_reviewed_never_deleted_and_medico_legal_ones_are_kept()
    {
        RetentionSchedule.Get(RetentionSchedule.OutpatientRecords).Action.ShouldBe(RetentionAction.Review);
        RetentionSchedule.Get(RetentionSchedule.InpatientRecords).Action.ShouldBe(RetentionAction.Review);
        var mlc = RetentionSchedule.Get(RetentionSchedule.MedicoLegalRecords);
        mlc.Action.ShouldBe(RetentionAction.Keep);
        mlc.Effective(5).ShouldBeNull();
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData(10, 10)]
    [InlineData(1, 3)]
    [InlineData(500, 99)]
    public void A_period_stays_between_its_legal_minimum_and_maximum(int? stored, int effective) =>
        RetentionSchedule.Get(RetentionSchedule.OutpatientRecords).Effective(stored).ShouldBe(effective);

    /// <summary>
    /// The DPDP Rules keep logs a year. At the shortest period a hospital can set, a log line is deleted only once it is
    /// a full calendar year old, on every day of three years, the one with 29 February 2028 among them.
    /// </summary>
    [Theory]
    [InlineData(RetentionSchedule.MessageLog)]
    [InlineData(RetentionSchedule.IntegrationLog)]
    [InlineData(RetentionSchedule.SignInLog)]
    public void Logs_are_kept_a_full_year_across_a_leap_day_too(string key)
    {
        var rule = RetentionSchedule.Get(key);
        var shortest = rule.Effective(1).ShouldNotBeNull();
        var days = Enumerable.Range(0, 3 * 365).Select(d => new DateTimeOffset(2027, 1, 1, 9, 0, 0, TimeSpan.Zero).AddDays(d));

        days.ShouldAllBe(now => rule.Cutoff(now, shortest) <= now.AddYears(-1));
    }

    [Fact]
    public void The_cut_off_is_counted_back_from_now()
    {
        RetentionSchedule.Get(RetentionSchedule.Outbox).Cutoff(Now, 30).ShouldBe(Now.AddDays(-30));
        RetentionSchedule.Get(RetentionSchedule.OutpatientRecords).Cutoff(Now, 3).ShouldBe(Now.AddYears(-3));
    }

    [Fact]
    public void Changing_a_medical_records_period_tells_Clinical()
    {
        var change = RetentionChange.Record(RetentionSchedule.Get(RetentionSchedule.InpatientRecords), 5, 10, null, Now);

        var raised = change.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<RecordRetentionChanged>();
        raised.Inpatient.ShouldBeTrue();
        raised.PreviousYears.ShouldBe(5);
        raised.Years.ShouldBe(10);
    }

    [Fact]
    public void Other_changes_raise_nothing() =>
        RetentionChange.Record(RetentionSchedule.Get(RetentionSchedule.Outbox), 30, 60, null, Now).DomainEvents.ShouldBeEmpty();
}
