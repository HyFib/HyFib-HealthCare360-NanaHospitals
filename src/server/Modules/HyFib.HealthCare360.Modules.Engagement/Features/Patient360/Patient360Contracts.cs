using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Patient360;

public sealed record Patient360Identity(
    Guid Id,
    string Uhid,
    string FullName,
    Gender Gender,
    string? Age,
    string? PhoneMasked,
    string? City,
    PatientCategory Category,
    string? PreferredLanguage,
    bool IsVip,
    bool IsDeceased,
    DateTimeOffset RegisteredAt);

/// <summary>Visits at this facility (cancelled visits excluded).</summary>
public sealed record VisitSummary(
    int Total,
    int LastTwelveMonths,
    DateTimeOffset? FirstVisitAt,
    DateTimeOffset? LastVisitAt,
    string? LastReferredBy,
    IReadOnlyList<CountByLabel> ByType,
    IReadOnlyList<CountByLabel> TopDepartments);

/// <summary>Finalized bills and receipts across the group (from Billing's events).</summary>
public sealed record SpendSummary(decimal TotalBilled, decimal TotalPaid, decimal BilledLastTwelveMonths, int Bills, DateTimeOffset? LastBilledAt);

public sealed record FeedbackSummary(int Responses, decimal? AverageRating, int? LastNps, Sentiment? LastSentiment, int LowScores, DateTimeOffset? LastSubmittedAt);

public sealed record ComplaintBrief(Guid Id, string ComplaintNumber, ComplaintStatus Status, ComplaintPriority Priority, string? Category, DateTimeOffset RaisedAt);

public sealed record FollowUpBrief(Guid Id, FollowUpType Type, DateOnly DueDate, FollowUpStatus Status, FollowUpPriority Priority, bool IsOverdue);

public sealed record CommunicationSummary(
    bool MarketingConsent,
    bool UpdatesConsent,
    DateTimeOffset? MarketingChangedAt,
    int CampaignsReceived,
    DateTimeOffset? LastCampaignAt,
    bool OptedOutByLink);

public sealed record LeadBrief(Guid Id, string LeadNumber, LeadSource Source, string? SourceDetail, DateTimeOffset CreatedAt, DateTimeOffset? ConvertedAt);

/// <summary>The CRM view of one patient (CRM-04): relationship, value, experience and communication choices.</summary>
public sealed record Patient360View(
    Patient360Identity Patient,
    VisitSummary Visits,
    SpendSummary Spend,
    FeedbackSummary Feedback,
    IReadOnlyList<ComplaintBrief> RecentComplaints,
    IReadOnlyList<FollowUpBrief> OpenFollowUps,
    CommunicationSummary Communication,
    LeadBrief? SourceLead);
