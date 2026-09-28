namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public enum CampaignChannel
{
    Sms = 0,
    WhatsApp,
}

public enum CampaignStatus
{
    Draft = 0,
    Scheduled,
    Sending,
    Completed,
    Cancelled,
}

/// <summary>Who a campaign is for. All criteria are optional; consent is always required on top (see CampaignAudienceQuery).</summary>
public sealed record CampaignSegment(
    int? MinAge,
    int? MaxAge,
    Gender? Gender,
    string? City,
    DateOnly? LastVisitFrom,
    DateOnly? LastVisitTo,
    IReadOnlyList<Guid> DepartmentIds)
{
    public static CampaignSegment Everyone { get; } = new(null, null, null, null, null, null, []);
}

/// <summary>
/// An SMS/WhatsApp campaign to a patient segment (CRM-03). Messages go only to patients with an active marketing
/// consent, are queued in batches through the notification outbox during the TRAI promotional window, and every
/// message carries a personal opt-out link.
/// </summary>
public sealed class Campaign : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CampaignChannel Channel { get; set; }

    /// <summary>Notification template key (its DLT template id lives on the template).</summary>
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>Text substituted for <c>{{message}}</c> in the template.</summary>
    public string Message { get; set; } = string.Empty;

    public CampaignStatus Status { get; set; }
    public int? SegmentMinAge { get; set; }
    public int? SegmentMaxAge { get; set; }
    public Gender? SegmentGender { get; set; }
    public string? SegmentCity { get; set; }
    public DateOnly? SegmentLastVisitFrom { get; set; }
    public DateOnly? SegmentLastVisitTo { get; set; }
    public List<Guid> SegmentDepartmentIds { get; set; } = [];
    public DateTimeOffset? ScheduledAt { get; set; }
    public Guid? ScheduledByUserId { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CancelReason { get; set; }

    /// <summary>Audience size when the campaign was scheduled (consent is re-checked at send time).</summary>
    public int AudienceCount { get; set; }

    public int QueuedCount { get; set; }
    public int SkippedCount { get; set; }

    public CampaignSegment Segment =>
        new(SegmentMinAge, SegmentMaxAge, SegmentGender, SegmentCity, SegmentLastVisitFrom, SegmentLastVisitTo, SegmentDepartmentIds);

    public bool IsEditable => Status == CampaignStatus.Draft;

    public void SetSegment(CampaignSegment segment)
    {
        SegmentMinAge = segment.MinAge;
        SegmentMaxAge = segment.MaxAge;
        SegmentGender = segment.Gender;
        SegmentCity = string.IsNullOrWhiteSpace(segment.City) ? null : segment.City.Trim();
        SegmentLastVisitFrom = segment.LastVisitFrom;
        SegmentLastVisitTo = segment.LastVisitTo;
        SegmentDepartmentIds = segment.DepartmentIds.Distinct().ToList();
    }

    public Result Schedule(DateTimeOffset at, int audienceCount, Guid? userId, DateTimeOffset now)
    {
        if (Status != CampaignStatus.Draft)
        {
            return Error.Rule("campaign.not_draft", "Only a draft campaign can be scheduled.");
        }

        if (at < now.AddMinutes(-1))
        {
            return Error.Field("scheduledAt", "Choose a time in the future.");
        }

        if (audienceCount <= 0)
        {
            return Error.Rule("campaign.empty_audience", "No consenting patients match this segment.");
        }

        Status = CampaignStatus.Scheduled;
        ScheduledAt = at;
        ScheduledByUserId = userId;
        AudienceCount = audienceCount;
        return Result.Success();
    }

    /// <summary>Takes a scheduled campaign back to draft for changes (before sending starts).</summary>
    public Result Unschedule()
    {
        if (Status != CampaignStatus.Scheduled)
        {
            return Error.Rule("campaign.not_scheduled", "Only a scheduled campaign that has not started can be taken back to draft.");
        }

        Status = CampaignStatus.Draft;
        ScheduledAt = null;
        ScheduledByUserId = null;
        return Result.Success();
    }

    public Result BeginSending(DateTimeOffset now)
    {
        if (Status != CampaignStatus.Scheduled || ScheduledAt > now)
        {
            return Error.Rule("campaign.not_due", "The campaign is not due for sending.");
        }

        Status = CampaignStatus.Sending;
        StartedAt = now;
        return Result.Success();
    }

    public void RecordBatch(int queued, int skipped)
    {
        QueuedCount += queued;
        SkippedCount += skipped;
    }

    public Result Complete(DateTimeOffset now)
    {
        if (Status != CampaignStatus.Sending)
        {
            return Error.Rule("campaign.not_sending", "Only a campaign that is sending can complete.");
        }

        Status = CampaignStatus.Completed;
        CompletedAt = now;
        return Result.Success();
    }

    /// <summary>Cancels before or during sending; messages already queued are delivered.</summary>
    public Result Cancel(string reason, DateTimeOffset now)
    {
        if (Status is CampaignStatus.Completed or CampaignStatus.Cancelled)
        {
            return Error.Rule("campaign.closed", "This campaign has already finished.");
        }

        Status = CampaignStatus.Cancelled;
        CancelReason = reason.Trim();
        CompletedAt = now;
        return Result.Success();
    }
}

public enum CampaignRecipientStatus
{
    Queued = 0,
    Skipped,
}

/// <summary>
/// One patient in a sent campaign: links to the notification log for delivery tracking and holds the hash of the
/// personal opt-out token included in the message. Append-only, so not data-audited.
/// </summary>
[NotAudited]
public sealed class CampaignRecipient : Entity
{
    public Guid CampaignId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? NotificationLogId { get; set; }
    public CampaignRecipientStatus Status { get; set; }
    public string? SkipReason { get; set; }
    public string OptOutTokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the patient used this message's opt-out link (campaign unsubscribe rate).</summary>
    public DateTimeOffset? OptedOutAt { get; set; }
}

/// <summary>
/// TRAI (TCCCPR 2018) allows promotional messages only between 10:00 and 21:00; campaigns wait for the window.
/// </summary>
public static class CampaignSendWindow
{
    public static readonly TimeOnly Opens = new(10, 0);
    public static readonly TimeOnly Closes = new(21, 0);

    public static bool IsOpen(DateTime localTime)
    {
        var time = TimeOnly.FromDateTime(localTime);
        return time >= Opens && time < Closes;
    }

    /// <summary>The first local time at or after <paramref name="localTime"/> when sending is allowed.</summary>
    public static DateTime NextOpening(DateTime localTime)
    {
        if (IsOpen(localTime))
        {
            return localTime;
        }

        var date = DateOnly.FromDateTime(localTime);
        return TimeOnly.FromDateTime(localTime) < Opens ? date.ToDateTime(Opens) : date.AddDays(1).ToDateTime(Opens);
    }
}
