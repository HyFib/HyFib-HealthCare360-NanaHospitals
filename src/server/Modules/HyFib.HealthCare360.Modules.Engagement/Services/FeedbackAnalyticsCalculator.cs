using System.Globalization;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features;
using HyFib.HealthCare360.Modules.Engagement.Features.Feedback;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>One feedback response, as analytics needs it.</summary>
public sealed record FeedbackPoint(
    DateTimeOffset SubmittedAt,
    FeedbackChannel Channel,
    Guid? DepartmentId,
    FeedbackRatings Ratings,
    int? Nps,
    Sentiment Sentiment,
    bool IsLowScore);

/// <summary>One complaint, as analytics needs it.</summary>
public sealed record ComplaintPoint(DateTimeOffset RaisedAt, DateTimeOffset DueAt, DateTimeOffset? ResolvedAt, ComplaintStatus Status);

/// <summary>
/// Pure feedback analytics (FB-04). Every response weighs the same in the average rating, so a response that rated
/// seven aspects does not count seven times as much as one that rated a single aspect. Monthly buckets use hospital-local
/// (IST) months. Turnaround is measured from when a complaint was raised to when it was resolved.
/// </summary>
public static class FeedbackAnalyticsCalculator
{
    private const string NotRecorded = "Not recorded";

    public static FeedbackAnalytics Compute(
        ReportPeriod period,
        IReadOnlyList<FeedbackPoint> points,
        IReadOnlyList<ComplaintPoint> complaints,
        IReadOnlyDictionary<Guid, string> departmentNames,
        DateTimeOffset now)
    {
        var aspects = Enum.GetValues<FeedbackAspect>()
            .Select(aspect =>
            {
                var ratings = points.Select(p => p.Ratings.For(aspect)).OfType<int>().ToList();
                return new AspectScore(aspect, ratings.Count, ratings.Count == 0 ? null : Round((decimal)ratings.Average()));
            })
            .Where(a => a.Responses > 0)
            .ToList();

        var trend = period.MonthKeys()
            .Select(month =>
            {
                var inMonth = points.Where(p => MonthOf(p.SubmittedAt) == month).ToList();
                return new MonthlyFeedback(month, inMonth.Count, NpsOf(inMonth).Score, AverageOf(inMonth), inMonth.Count(p => p.IsLowScore));
            })
            .ToList();

        var departments = points
            .GroupBy(p => p.DepartmentId)
            .Select(g => new DepartmentScore(
                g.Key,
                g.Key is { } id ? departmentNames.GetValueOrDefault(id, "Other") : NotRecorded,
                g.Count(),
                AverageOf(g),
                NpsOf(g).Score))
            .OrderByDescending(d => d.Responses).ThenBy(d => d.Department, StringComparer.Ordinal)
            .ToList();

        return new FeedbackAnalytics(
            period.From,
            period.To,
            points.Count,
            NpsOf(points),
            AverageOf(points),
            points.Count(p => p.IsLowScore),
            aspects,
            Enum.GetValues<Sentiment>().Select(s => new CountByLabel(s.ToString(), points.Count(p => p.Sentiment == s))).ToList(),
            Enum.GetValues<FeedbackChannel>().Select(c => new CountByLabel(c.ToString(), points.Count(p => p.Channel == c))).Where(c => c.Count > 0).ToList(),
            trend,
            departments,
            ComplaintStatsOf(complaints, now));
    }

    public static ComplaintStats ComplaintStatsOf(IReadOnlyList<ComplaintPoint> complaints, DateTimeOffset now)
    {
        var open = complaints.Where(c => c.Status is ComplaintStatus.Open or ComplaintStatus.InProgress).ToList();
        var resolved = complaints.Where(c => c.ResolvedAt is not null).ToList();
        return new ComplaintStats(
            complaints.Count,
            open.Count,
            open.Count(c => now > c.DueAt),
            resolved.Count,
            resolved.Count == 0 ? null : Math.Round(resolved.Average(c => (c.ResolvedAt!.Value - c.RaisedAt).TotalHours), 1, MidpointRounding.AwayFromZero),
            resolved.Count == 0 ? null : Math.Round(resolved.Count(c => c.ResolvedAt <= c.DueAt) * 100m / resolved.Count, 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>Mean of each response's own average rating.</summary>
    public static decimal? AverageOf(IEnumerable<FeedbackPoint> points)
    {
        var averages = points.Select(p => p.Ratings.Average).OfType<decimal>().ToList();
        return averages.Count == 0 ? null : Round(averages.Average());
    }

    private static NpsResult NpsOf(IEnumerable<FeedbackPoint> points) =>
        NpsCalculator.Compute(points.Select(p => p.Nps).OfType<int>());

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string MonthOf(DateTimeOffset instant) =>
        EngagementClock.ToLocal(instant).ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
