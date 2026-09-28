using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Feedback;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class FeedbackAnalyticsTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Cardiology = Guid.NewGuid();

    private static FeedbackPoint Point(
        DateTimeOffset at,
        int? doctor = null,
        int? overall = null,
        int? nps = null,
        Sentiment sentiment = Sentiment.Neutral,
        bool low = false,
        FeedbackChannel channel = FeedbackChannel.QrCode,
        Guid? department = null) =>
        new(at, channel, department, new FeedbackRatings(doctor, null, null, null, null, null, overall), nps, sentiment, low);

    [Fact]
    public void The_default_period_is_the_last_twelve_calendar_months()
    {
        var period = ReportPeriod.Resolve(null, null, Today).Value;

        period.From.ShouldBe(new DateOnly(2025, 10, 1));
        period.To.ShouldBe(Today);
        period.MonthKeys().Count.ShouldBe(12);
        period.MonthKeys()[0].ShouldBe("2025-10");
        period.MonthKeys()[^1].ShouldBe("2026-09");
    }

    [Fact]
    public void Periods_must_run_forwards_and_stay_within_three_years()
    {
        ReportPeriod.Resolve(Today, Today.AddDays(-1), Today).Error!.Fields!.ShouldContainKey("to");
        ReportPeriod.Resolve(Today.AddYears(-4), Today, Today).Error!.Fields!.ShouldContainKey("from");
        ReportPeriod.Resolve(Today.AddYears(-3), Today, Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Period_bounds_are_india_midnights()
    {
        var period = new ReportPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

        period.SinceUtc.ShouldBe(new DateTimeOffset(2026, 8, 31, 18, 30, 0, TimeSpan.Zero));
        period.UntilUtc.ShouldBe(new DateTimeOffset(2026, 9, 30, 18, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Every_response_weighs_the_same_in_the_average_rating()
    {
        FeedbackPoint[] points = [Point(Now, doctor: 5, overall: 5), Point(Now, doctor: 1)];

        FeedbackAnalyticsCalculator.AverageOf(points).ShouldBe(3m);
    }

    [Fact]
    public void Analytics_summarise_nps_aspects_sentiment_channels_and_departments()
    {
        var period = new ReportPeriod(new DateOnly(2026, 8, 1), Today);
        FeedbackPoint[] points =
        [
            Point(new DateTimeOffset(2026, 8, 5, 5, 0, 0, TimeSpan.Zero), doctor: 5, overall: 5, nps: 10, sentiment: Sentiment.Positive, department: Cardiology),
            Point(new DateTimeOffset(2026, 9, 2, 5, 0, 0, TimeSpan.Zero), doctor: 2, overall: 2, nps: 3, sentiment: Sentiment.Negative, low: true,
                channel: FeedbackChannel.SmsLink, department: Cardiology),
            Point(new DateTimeOffset(2026, 9, 3, 5, 0, 0, TimeSpan.Zero), overall: 4, nps: 8),
        ];

        var result = FeedbackAnalyticsCalculator.Compute(period, points, [], new Dictionary<Guid, string> { [Cardiology] = "Cardiology" }, Now);

        result.Responses.ShouldBe(3);
        result.Nps.Score.ShouldBe(0m);
        result.LowScores.ShouldBe(1);
        result.Aspects.Select(a => (a.Aspect, a.Responses, a.Average)).ShouldBe(
            [(FeedbackAspect.Doctor, 2, 3.5m), (FeedbackAspect.Overall, 3, 3.67m)]);
        result.BySentiment.Select(s => (s.Label, s.Count)).ShouldBe([("Negative", 1), ("Neutral", 1), ("Positive", 1)]);
        result.ByChannel.Select(c => (c.Label, c.Count)).ShouldBe([("QrCode", 2), ("SmsLink", 1)]);
        result.Trend.Select(m => (m.Month, m.Responses, m.LowScores)).ShouldBe([("2026-08", 1, 0), ("2026-09", 2, 1)]);
        result.Departments.Select(d => (d.Department, d.Responses)).ShouldBe([("Cardiology", 2), ("Not recorded", 1)]);
    }

    [Fact]
    public void Months_are_india_months()
    {
        // 31 August 20:00 UTC is 1 September 01:30 IST.
        var period = new ReportPeriod(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30));
        FeedbackPoint[] points = [Point(new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero), overall: 5)];

        var trend = FeedbackAnalyticsCalculator.Compute(period, points, [], new Dictionary<Guid, string>(), Now).Trend;

        trend.Single(m => m.Month == "2026-09").Responses.ShouldBe(1);
    }

    [Fact]
    public void Complaint_stats_report_backlog_turnaround_and_sla()
    {
        ComplaintPoint[] complaints =
        [
            new(Now.AddHours(-100), Now.AddHours(-28), Now.AddHours(-90), ComplaintStatus.Closed),
            new(Now.AddHours(-50), Now.AddHours(-26), Now.AddHours(-20), ComplaintStatus.Resolved),
            new(Now.AddHours(-30), Now.AddHours(-6), null, ComplaintStatus.InProgress),
            new(Now.AddHours(-2), Now.AddHours(70), null, ComplaintStatus.Open),
        ];

        var stats = FeedbackAnalyticsCalculator.ComplaintStatsOf(complaints, Now);

        stats.ShouldBe(new ComplaintStats(4, 2, 1, 2, 20.0, 50.0m));
    }

    [Fact]
    public void No_resolved_complaints_means_no_turnaround()
    {
        var stats = FeedbackAnalyticsCalculator.ComplaintStatsOf([], Now);

        stats.AverageTurnaroundHours.ShouldBeNull();
        stats.ResolvedWithinSlaPercent.ShouldBeNull();
    }
}
