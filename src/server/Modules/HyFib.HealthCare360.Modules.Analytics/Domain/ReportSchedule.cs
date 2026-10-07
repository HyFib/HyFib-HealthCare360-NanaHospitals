namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>
/// A report file's format. Stored as text (varchar 64), so a new format goes at the end and needs no migration.
/// </summary>
public enum ExportFormat
{
    Xlsx = 0,
    Csv,

    /// <summary>The printable A4 document, made on the server by the PDF renderer (Reports/Export/ReportPdf).</summary>
    Pdf,
}

/// <summary>Where a schedule's file goes. Stored as text, so a new destination goes at the end.</summary>
public enum ScheduleDestination
{
    /// <summary>Attached to an e-mail to the recipients (MIS-05, BI-04).</summary>
    Email = 0,

    /// <summary>
    /// Written as a CSV file to the BI extract folder, which external BI tools such as Power BI read (BI-05). The file is
    /// replaced at every run, so the tool always reads the latest data.
    /// </summary>
    ExtractFolder,
}

/// <summary>How fresh a BI extract's data is, as its list and the refresh log show it.</summary>
public enum ExtractState
{
    /// <summary>Set up, and not produced yet: the first refresh is still to come.</summary>
    Waiting = 0,

    /// <summary>Refreshed on schedule.</summary>
    Fresh,

    /// <summary>Not refreshed for longer than its schedule allows (<see cref="ReportSchedule.IsLate"/>).</summary>
    Late,

    /// <summary>The last refresh failed: BI tools read the previous file.</summary>
    Failing,

    /// <summary>Not refreshed while paused.</summary>
    Paused,
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
/// A report run on a schedule: e-mailed as an Excel, CSV or PDF attachment (MIS-05, BI-04), or written to the BI extract
/// folder as a CSV file for Power BI and similar tools (<see cref="ScheduleDestination.ExtractFolder"/>, BI-05). It
/// belongs to one facility and runs with the permissions of the person who last saved it, re-checked at every run, so
/// revoking someone's access also stops their scheduled reports. Parameters are stored in canonical form with a relative
/// period ("yesterday", "this financial year") so every run covers new dates.
/// </summary>
public sealed class ReportSchedule : AuditableEntity, IFacilityScoped
{
    public const int MaxRecipients = 20;

    /// <summary>Longest extract file name (without ".csv").</summary>
    public const int MaxExtractNameLength = 64;

    public Guid FacilityId { get; set; }

    public ScheduleDestination Destination { get; set; }

    /// <summary>
    /// The file a BI extract writes, without ".csv": lower-case letters, digits and dashes, unique in the facility. Null
    /// for e-mails.
    /// </summary>
    public string? ExtractName { get; set; }

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

    /// <summary>
    /// When the file was last produced: how fresh the data is that a BI tool reads from an extract. Cleared when an
    /// extract's file changes (another name, report or inputs), until a refresh writes the new one.
    /// </summary>
    public DateTimeOffset? LastSucceededAt { get; set; }

    public bool IsExtract => Destination == ScheduleDestination.ExtractFolder;

    /// <summary>Whether the last run went wrong (failed, or skipped because the owner can no longer sign in).</summary>
    public bool IsFailing => LastStatus is ScheduleRunStatus.Failed or ScheduleRunStatus.Skipped;

    /// <summary>Recomputes <see cref="NextRunAt"/> (none while paused).</summary>
    public void Reschedule(DateTimeOffset now) =>
        NextRunAt = IsActive ? ScheduleCalendar.NextRun(Frequency, TimeOfDay, DayOfWeek, DayOfMonth, now) : null;

    public void RecordRun(DateTimeOffset at, ScheduleRunStatus status, string? error)
    {
        LastRunAt = at;
        LastStatus = status;
        LastError = error is { Length: > 500 } ? error[..500] : error;
        if (status == ScheduleRunStatus.Succeeded)
        {
            LastSucceededAt = at;
        }
    }

    /// <summary>
    /// An active extract whose data a BI tool reads is older than one interval of its schedule and a margin (or that has
    /// not been produced in that time since it was set up or changed): a refresh failed, or the server was off.
    /// </summary>
    public bool IsLate(DateTimeOffset now) =>
        IsExtract && IsActive && (LastSucceededAt ?? UpdatedAt ?? CreatedAt) < now - ScheduleCalendar.Interval(Frequency) - ScheduleCalendar.LateMargin;

    /// <summary>How fresh the extract's data is; failing comes before late, as it says why.</summary>
    public ExtractState State(DateTimeOffset now) =>
        !IsActive ? ExtractState.Paused
        : IsFailing ? ExtractState.Failing
        : IsLate(now) ? ExtractState.Late
        : LastSucceededAt is null ? ExtractState.Waiting
        : ExtractState.Fresh;

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
