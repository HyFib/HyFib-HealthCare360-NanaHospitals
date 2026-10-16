using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

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

/// <summary>What a line of the communications history is.</summary>
public enum CommunicationKind
{
    /// <summary>A service message about the patient's care: appointment reminder, report ready, bill, follow-up due…</summary>
    Message = 0,

    /// <summary>A marketing campaign message, or a campaign that passed the patient over.</summary>
    Campaign,

    /// <summary>A follow-up call task the CRM team has called on (attempts and the last outcome).</summary>
    FollowUpCall,
}

/// <summary>
/// One line of the patient's communications history (CRM-04). A message carries what was sent, on which channel, to a
/// masked address and how far it got, but never its text, which the server does not keep. A follow-up call is one line
/// per task with its attempts and last outcome, because attempts are not kept one by one.
/// </summary>
/// <param name="OccurredAt">When the message was queued, or when the call task last changed.</param>
/// <param name="TemplateKey">The notification template sent (messages and campaigns).</param>
/// <param name="Purpose">The template's description, when it has one, to say what the message was for.</param>
/// <param name="Recipient">The number or address, masked as the notification log keeps it.</param>
/// <param name="SkipReason">Why a campaign passed the patient over (no consent, no number…).</param>
public sealed record CommunicationEntry(
    Guid Id,
    CommunicationKind Kind,
    DateTimeOffset OccurredAt,
    NotificationChannel? Channel,
    string? TemplateKey,
    string? Purpose,
    string? Recipient,
    NotificationStatus? Status,
    DateTimeOffset? SentAt,
    DateTimeOffset? DeliveredAt,
    Guid? CampaignId,
    string? CampaignName,
    string? SkipReason,
    Guid? FollowUpId,
    FollowUpType? FollowUpType,
    FollowUpStatus? FollowUpStatus,
    FollowUpOutcome? CallOutcome,
    int? CallAttempts);
