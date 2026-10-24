using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>SRS SEC-06: a chart is issued to come back today or later, by the hospital's day in India.</summary>
public sealed class RecordIssueRequestTests
{
    [Theory]
    [InlineData("2026-10-23T19:00:00Z", "2026-10-24", true)] // 00:30 on the 24th in India
    [InlineData("2026-10-23T19:00:00Z", "2026-10-23", false)]
    [InlineData("2026-10-23T18:00:00Z", "2026-10-23", true)] // 23:30 on the 23rd in India
    public void A_chart_is_due_back_today_or_later(string utcNow, string dueBackOn, bool valid)
    {
        var validator = new IssueRecordRequestValidator(new Clock(DateTimeOffset.Parse(utcNow, System.Globalization.CultureInfo.InvariantCulture)));

        validator.Validate(new IssueRecordRequest(DateOnly.Parse(dueBackOn, System.Globalization.CultureInfo.InvariantCulture))).IsValid.ShouldBe(valid);
        validator.Validate(new IssueRecordRequest(null)).IsValid.ShouldBeTrue();
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
