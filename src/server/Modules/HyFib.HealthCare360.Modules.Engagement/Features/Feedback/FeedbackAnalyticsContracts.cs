using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Feedback;

public sealed record AspectScore(FeedbackAspect Aspect, int Responses, decimal? Average);

public sealed record MonthlyFeedback(string Month, int Responses, decimal? Nps, decimal? AverageRating, int LowScores);

public sealed record DepartmentScore(Guid? DepartmentId, string Department, int Responses, decimal? AverageRating, decimal? Nps);

/// <summary>Complaint handling in the period: volume, backlog and turnaround against the SLA.</summary>
public sealed record ComplaintStats(int Raised, int Open, int Overdue, int Resolved, double? AverageTurnaroundHours, decimal? ResolvedWithinSlaPercent);

/// <summary>Feedback analytics (FB-04): NPS, ratings per aspect, sentiment, channels, monthly trend, departments, complaints.</summary>
public sealed record FeedbackAnalytics(
    DateOnly From,
    DateOnly To,
    int Responses,
    NpsResult Nps,
    decimal? AverageRating,
    int LowScores,
    IReadOnlyList<AspectScore> Aspects,
    IReadOnlyList<CountByLabel> BySentiment,
    IReadOnlyList<CountByLabel> ByChannel,
    IReadOnlyList<MonthlyFeedback> Trend,
    IReadOnlyList<DepartmentScore> Departments,
    ComplaintStats Complaints);
