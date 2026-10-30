using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>
/// SRS SEC-06: a chart is issued to come back today or later, by the hospital's day in India; a copy holds each document
/// once; and a SHA-256 to look for is taken as people copy it from a certificate or a tool.
/// </summary>
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

    [Fact]
    public void A_copy_holds_each_document_once_and_at_most_fifty()
    {
        var validator = new IssueRecordRequestValidator(new Clock(DateTimeOffset.UtcNow));
        var report = new RecordCopyDocument("diagnostics.lab-report", Guid.CreateVersion7());
        RecordCopyDocument[] fifty = [.. Enumerable.Range(0, 50).Select(_ => new RecordCopyDocument("clinical.prescription", Guid.CreateVersion7()))];

        validator.Validate(new IssueRecordRequest(null, [report, new RecordCopyDocument("clinical.mlc", Guid.CreateVersion7())])).IsValid.ShouldBeTrue();
        validator.Validate(new IssueRecordRequest(null, fifty)).IsValid.ShouldBeTrue();
        validator.Validate(new IssueRecordRequest(null, [.. fifty, report])).IsValid.ShouldBeFalse();
        validator.Validate(new IssueRecordRequest(null, [report, report with { }])).IsValid.ShouldBeFalse();
        validator.Validate(new IssueRecordRequest(null, [report with { Kind = "" }])).IsValid.ShouldBeFalse();
        validator.Validate(new IssueRecordRequest(null, [report with { Id = Guid.Empty }])).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", true)]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", true)]
    [InlineData("9f86d081 884c7d65 9a2feaa0 c55ad015 a3bf4f1b 2b0b822c d15d6c15 b0f00a08", true)]
    [InlineData("9f:86:d0:81:88:4c:7d:65:9a:2f:ea:a0:c5:5a:d0:15:a3:bf:4f:1b:2b:0b:82:2c:d1:5d:6c:15:b0:f0:0a:08", true)]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0", false)]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0g", false)]
    [InlineData("", false)]
    public void A_sha256_is_looked_for_as_people_copy_it(string value, bool valid)
    {
        new VerifyRecordCopyRequestValidator().Validate(new VerifyRecordCopyRequest(value)).IsValid.ShouldBe(valid);
        RecordCopyFile.NormalizeSha256(value).ShouldBe(valid ? "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08" : null);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
