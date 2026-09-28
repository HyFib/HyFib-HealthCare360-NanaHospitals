namespace HyFib.HealthCare360.Modules.Analytics.Domain;

public enum ExportFormat
{
    Xlsx = 0,
    Csv,
}

public enum ScheduleRunStatus
{
    /// <summary>The file was generated and the e-mails were queued.</summary>
    Succeeded = 0,
    Failed,

    /// <summary>Not run (for example the owner's account was disabled).</summary>
    Skipped,
}

/// <summary>
/// A report e-mailed on a schedule (MIS-05, BI-04) as an Excel or CSV attachment. It belongs to one facility and runs
/// with the permissions of the person who last saved it, re-checked at every run, so revoking someone's access also
/// stops their scheduled e-mails. Parameters are stored in canonical form with a relative period ("yesterday",
/// "last month") so every e-mail covers new dates.
/// </summary>
public sealed class ReportSchedule : AuditableEntity, IFacilityScoped
{
    public const int MaxRecipients = 20;

    public Guid FacilityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ReportId { get; set; } = string.Empty;

    /// <summary>Canonical report inputs as JSON (<see cref="Reports.ReportArguments.ToCanonical"/>).</summary>
    public string ParametersJson { get; set; } = "{}";

    public ExportFormat Format { get; set; }
    public ScheduleFrequency Frequency { get; set; }

    /// <summary>IST wall-clock time of the e-mail.</summary>
    public TimeOnly TimeOfDay { get; set; }

    public Weekday? DayOfWeek { get; set; }

    /// <summary>1–28, or 31 for the last day of the month.</summary>
    public int? DayOfMonth { get; set; }

    public List<string> Recipients { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public Guid OwnerUserId { get; set; }
    public string OwnerName { get; set; } = string.Empty;
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public ScheduleRunStatus? LastStatus { get; set; }
    public string? LastError { get; set; }

    /// <summary>Recomputes <see cref="NextRunAt"/> (none while paused).</summary>
    public void Reschedule(DateTimeOffset now) =>
        NextRunAt = IsActive ? ScheduleCalendar.NextRun(Frequency, TimeOfDay, DayOfWeek, DayOfMonth, now) : null;

    public void RecordRun(DateTimeOffset at, ScheduleRunStatus status, string? error)
    {
        LastRunAt = at;
        LastStatus = status;
        LastError = error is { Length: > 500 } ? error[..500] : error;
    }

    public string Describe() => ScheduleCalendar.Describe(Frequency, TimeOfDay, DayOfWeek, DayOfMonth);
}

public enum DeliveryStatus
{
    /// <summary>The report is being generated.</summary>
    Generating = 0,

    /// <summary>The file was generated and one e-mail per recipient was queued.</summary>
    Ready,
    Failed,
    Skipped,
}

public enum DeliveryTrigger
{
    Scheduled = 0,

    /// <summary>"Send now" from the schedules screen.</summary>
    Manual,
}

public enum RecipientStatus
{
    Pending = 0,
    Sent,

    /// <summary>Not sent: the file was purged or the schedule deleted before delivery.</summary>
    Cancelled,
}

/// <summary>
/// One generated file of a schedule and its e-mails. The file is kept AES-GCM encrypted (IFieldProtector) so it can be
/// attached to the e-mails by the outbox and downloaded again from HC360; the content is purged after the retention
/// period. Append-only, so it is not data-audited (the export itself is audited).
/// </summary>
[NotAudited]
public sealed class ReportDelivery : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ScheduleId { get; set; }
    public string ReportId { get; set; } = string.Empty;
    public string ReportTitle { get; set; } = string.Empty;
    public DeliveryTrigger Trigger { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    public DeliveryStatus Status { get; set; }
    public ExportFormat Format { get; set; }
    public string? FileName { get; set; }
    public string? ContentType { get; set; }
    public long SizeBytes { get; set; }
    public int RowCount { get; set; }

    /// <summary>Encrypted file content; null once purged.</summary>
    public byte[]? Content { get; set; }

    public DateTimeOffset? GeneratedAt { get; set; }
    public DateTimeOffset? ContentPurgedAt { get; set; }
    public string? Error { get; set; }
    public List<ReportDeliveryRecipient> Recipients { get; set; } = [];

    public void Fail(string error, DeliveryStatus status = DeliveryStatus.Failed)
    {
        Status = status;
        Error = error.Length > 1000 ? error[..1000] : error;
    }
}

/// <summary>One e-mail of a delivery (sent through the outbox, retried while the mail server is unreachable).</summary>
[NotAudited]
public sealed class ReportDeliveryRecipient : Entity
{
    public Guid DeliveryId { get; set; }
    public string Email { get; set; } = string.Empty;
    public RecipientStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    public void RecordAttempt(bool sent, string? error, DateTimeOffset now)
    {
        Attempts++;
        if (sent)
        {
            Status = RecipientStatus.Sent;
            SentAt = now;
            LastError = null;
        }
        else
        {
            LastError = error is { Length: > 500 } ? error[..500] : error;
        }
    }
}
