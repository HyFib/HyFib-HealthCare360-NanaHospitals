namespace HyFib.HealthCare360.Modules.Privacy.Domain;

public enum BreachStatus
{
    /// <summary>Recorded; the Data Protection Board has not been told yet.</summary>
    Open = 0,

    /// <summary>The Board has been told, without delay, what is known so far.</summary>
    BoardInformed,

    /// <summary>The detailed report has gone to the Board.</summary>
    ReportSubmitted,
    Closed,
}

/// <summary>What the breach affected.</summary>
public enum BreachNature
{
    /// <summary>Personal data was seen, taken or disclosed by someone who should not have it.</summary>
    Confidentiality = 0,

    /// <summary>Personal data was changed or corrupted.</summary>
    Integrity,

    /// <summary>Personal data was lost or could not be reached.</summary>
    Availability,
}

public enum BreachDataCategory
{
    Identity = 0,
    Contact,
    Health,
    Financial,
    GovernmentId,
    Credentials,

    /// <summary>Data of children, which the law protects more strictly.</summary>
    Children,
}

/// <summary>
/// A personal data breach (DPDP Act 2023 s8(6); DPDP Rules 2025, rule 7). The hospital tells the Data Protection
/// Board without delay, and sends the detailed report within 72 hours of becoming aware (longer only if the Board
/// allows it). Each affected person is told, without delay, what happened, the likely consequences, what the hospital
/// is doing, what they can do to protect themselves, and whom to contact. The register is group-wide, not per facility.
/// </summary>
public sealed class Breach : AuditableEntity
{
    /// <summary>The time the Rules give for the detailed report, from the moment the hospital became aware.</summary>
    public static readonly TimeSpan ReportWindow = TimeSpan.FromHours(72);

    /// <summary>"DPB{YY}{SEQ:4}".</summary>
    public string BreachNumber { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>What happened: its nature, extent, timing and location.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>When the hospital became aware of the breach; the 72-hour clock starts here.</summary>
    public DateTimeOffset AwareAt { get; set; }

    public DateTimeOffset? OccurredFrom { get; set; }
    public DateTimeOffset? OccurredTo { get; set; }
    public List<string> Nature { get; set; } = [];

    /// <summary>The systems, places or suppliers involved.</summary>
    public string? Location { get; set; }

    public List<string> DataCategories { get; set; } = [];
    public int? EstimatedAffected { get; set; }

    /// <summary>The consequences likely for the people affected.</summary>
    public string? Consequences { get; set; }

    /// <summary>What the hospital has done, and is doing, to reduce the risk.</summary>
    public string? Mitigation { get; set; }

    /// <summary>What the people affected can do to protect themselves.</summary>
    public string? PrincipalSteps { get; set; }

    /// <summary>The events, circumstances and reasons that led to the breach, and any findings about who caused it.</summary>
    public string? Cause { get; set; }

    /// <summary>What has been done so it does not happen again.</summary>
    public string? Remediation { get; set; }

    /// <summary>The short notice sent by SMS; the full notice is in the patient portal and the e-mail.</summary>
    public string? PrincipalNotice { get; set; }

    public BreachStatus Status { get; private set; }
    public DateTimeOffset? BoardInformedAt { get; private set; }

    /// <summary>A later deadline the Board allowed for the detailed report, on the hospital's written request.</summary>
    public DateTimeOffset? ExtendedUntil { get; private set; }

    public DateTimeOffset? ReportSubmittedAt { get; private set; }
    public string? BoardReference { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public string? ClosingNote { get; private set; }

    /// <summary>When the first notices to the people affected were queued.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }

    public DateTimeOffset ReportDueAt => ExtendedUntil ?? AwareAt + ReportWindow;

    public bool IsReportOverdue(DateTimeOffset now) => Status is BreachStatus.Open or BreachStatus.BoardInformed && now > ReportDueAt;

    /// <summary>The Board was told of the breach (the intimation rule 7(2)(a) asks for without delay).</summary>
    public Result RecordBoardInformed(DateTimeOffset at, string? reference, DateTimeOffset now)
    {
        if (Status != BreachStatus.Open)
        {
            return Error.Rule("privacy.breach_not_open", "The Board has already been told about this breach.");
        }

        if (at < AwareAt || at > now)
        {
            return Error.Field("at", "Enter when the Board was told: after the hospital became aware, and not in the future.");
        }

        Status = BreachStatus.BoardInformed;
        BoardInformedAt = at;
        BoardReference = Clean(reference) ?? BoardReference;
        return Result.Success();
    }

    /// <summary>The Board allowed more time for the detailed report.</summary>
    public Result RecordExtension(DateTimeOffset until, string? reference)
    {
        if (Status is BreachStatus.ReportSubmitted or BreachStatus.Closed)
        {
            return Error.Rule("privacy.breach_reported", "The detailed report has already been sent.");
        }

        if (until <= AwareAt + ReportWindow)
        {
            return Error.Field("until", "An extension must end after the usual 72 hours.");
        }

        ExtendedUntil = until;
        BoardReference = Clean(reference) ?? BoardReference;
        return Result.Success();
    }

    /// <summary>
    /// The detailed report went to the Board. It must cover what rule 7(2)(b) asks for, so those parts of the record
    /// have to be filled in first.
    /// </summary>
    public Result RecordReportSubmitted(DateTimeOffset at, string? reference, DateTimeOffset now)
    {
        if (Status != BreachStatus.BoardInformed)
        {
            return Error.Rule("privacy.breach_not_informed", Status == BreachStatus.Open
                ? "Record when the Board was first told before recording the detailed report."
                : "The detailed report has already been recorded.");
        }

        if (at < (BoardInformedAt ?? AwareAt) || at > now)
        {
            return Error.Field("at", "Enter when the report was sent: after the Board was first told, and not in the future.");
        }

        var missing = MissingForReport();
        if (missing.Count > 0)
        {
            return Error.Rule("privacy.breach_report_incomplete", $"Fill in {string.Join(", ", missing)} before recording the report.");
        }

        Status = BreachStatus.ReportSubmitted;
        ReportSubmittedAt = at;
        BoardReference = Clean(reference) ?? BoardReference;
        return Result.Success();
    }

    /// <summary>
    /// Closes the record. A breach that was never reported can be closed only with a note saying why (for example,
    /// no personal data was involved after all).
    /// </summary>
    public Result Close(string? note, DateTimeOffset now)
    {
        if (Status == BreachStatus.Closed)
        {
            return Error.Rule("privacy.breach_closed", "This breach is already closed.");
        }

        if (Status != BreachStatus.ReportSubmitted && string.IsNullOrWhiteSpace(note))
        {
            return Error.Field("note", "The detailed report was not sent. Say why the breach can be closed.");
        }

        Status = BreachStatus.Closed;
        ClosedAt = now;
        ClosingNote = Clean(note);
        return Result.Success();
    }

    /// <summary>The parts of the record rule 7(2)(b) asks the detailed report to cover that are still empty.</summary>
    public IReadOnlyList<string> MissingForReport() => Missing(
        (Description, "what happened"),
        (Consequences, "the likely consequences"),
        (Mitigation, "what is being done to reduce the risk"),
        (Cause, "the cause"),
        (Remediation, "what stops it happening again"));

    /// <summary>The parts of the record rule 7(1) asks each notice to the people affected to carry that are still empty.</summary>
    public IReadOnlyList<string> MissingForNotice() => Missing(
        (Description, "what happened"),
        (Consequences, "the likely consequences"),
        (Mitigation, "what is being done to reduce the risk"),
        (PrincipalSteps, "what people can do to protect themselves"),
        (PrincipalNotice, "the short notice for SMS"));

    private static List<string> Missing(params (string? Value, string Name)[] parts) =>
        [.. parts.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Name)];

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A patient affected by a breach, and when their notice was queued.</summary>
public sealed class BreachAffectedPatient : Entity
{
    public Guid BreachId { get; set; }
    public Guid PatientId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public Guid? AddedByUserId { get; set; }
    public DateTimeOffset? NotifiedAt { get; set; }

    /// <summary>How the notice went: the channels used, or why none could be ("no contact details").</summary>
    public string? NoticeChannels { get; set; }
}
