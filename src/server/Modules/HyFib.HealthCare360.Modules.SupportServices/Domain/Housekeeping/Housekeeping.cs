namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Housekeeping;

/// <summary>How much an area's cleaning matters to infection control (Kayakalp and the NABH housekeeping standards).</summary>
public enum CleaningRiskZone
{
    /// <summary>Theatres, ICUs, isolation, labour room, dialysis: cleaned most often.</summary>
    High = 0,

    /// <summary>Wards, casualty, the laboratory.</summary>
    Moderate,

    /// <summary>Corridors, waiting areas, offices.</summary>
    Low,
}

public enum CleaningFrequency
{
    /// <summary>Every day, a set number of times.</summary>
    Daily = 0,

    /// <summary>On set days of the week.</summary>
    Weekly,
}

/// <summary>One item of a cleaning checklist and whether it was done; an item not done says why.</summary>
public sealed record ChecklistResult(string Item, bool Done, string? Note = null);

/// <summary>
/// An area the housekeeping team cleans on a schedule (daily, so many times a day, or on set days of the week), with
/// the checklist each cleaning is done against.
/// </summary>
public sealed class CleaningArea : AuditableEntity, IFacilityScoped
{
    public const int MaxTimesPerDay = 6;

    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>The ward the area belongs to, when it is one.</summary>
    public Guid? WardId { get; set; }

    public CleaningRiskZone RiskZone { get; set; }
    public CleaningFrequency Frequency { get; set; }

    /// <summary>For a daily area: how many times a day it is cleaned.</summary>
    public int TimesPerDay { get; set; } = 1;

    /// <summary>For a weekly area: the days it is cleaned.</summary>
    public List<DayOfWeek> WeekDays { get; set; } = [];

    public List<string> Checklist { get; set; } = [];
    public bool IsActive { get; set; } = true;

    /// <summary>The cleanings due on a day: 1 to <see cref="TimesPerDay"/> for a daily area, one on its days for a weekly area.</summary>
    public IReadOnlyList<int> SlotsOn(DateOnly date) =>
        !IsActive ? []
        : Frequency == CleaningFrequency.Daily ? Enumerable.Range(1, Math.Clamp(TimesPerDay, 1, MaxTimesPerDay)).ToList()
        : WeekDays.Contains(date.DayOfWeek) ? [1]
        : [];
}

public enum CleaningStatus
{
    /// <summary>Done by the cleaner, waiting for a supervisor to look.</summary>
    AwaitingVerification = 0,

    /// <summary>A supervisor, someone other than the cleaner, found it done.</summary>
    Verified,

    /// <summary>A supervisor found it not done: it is cleaned again.</summary>
    Redo,
}

/// <summary>
/// Rules every cleaning shares, scheduled or asked for: the checklist answered item by item (an item not done says why),
/// and the verification made only by someone other than the cleaner.
/// </summary>
public static class CleaningRules
{
    public static Result CheckList(IReadOnlyList<string> expected, IReadOnlyList<ChecklistResult> answered)
    {
        var missing = expected.Where(item => !answered.Any(a => string.Equals(a.Item, item, StringComparison.Ordinal))).ToList();
        if (missing.Count > 0)
        {
            return Error.Field("checklist", $"Answer every item of the checklist: {string.Join(", ", missing)}.");
        }

        var unexplained = answered.FirstOrDefault(a => !a.Done && string.IsNullOrWhiteSpace(a.Note));
        return unexplained is not null
            ? Error.Field("checklist", $"Say why \"{unexplained.Item}\" was not done.")
            : Result.Success();
    }

    /// <summary>The supervisor who verifies a cleaning is never the one who did it.</summary>
    public static Result Verifier(Guid? cleanedBy, Guid? verifier) =>
        verifier is null || verifier == cleanedBy
            ? Error.Forbidden("housekeeping.verify_own", "You did this cleaning, so you cannot verify it. Another supervisor checks it.")
            : Result.Success();
}

/// <summary>A scheduled cleaning of an area, done against its checklist and verified by a supervisor who did not do it.</summary>
public sealed class CleaningRecord : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AreaId { get; set; }
    public DateOnly Date { get; set; }
    public int Slot { get; set; }
    public DateTimeOffset CleanedAt { get; set; }
    public Guid? CleanedBy { get; set; }
    public string CleanedByName { get; set; } = string.Empty;
    public List<ChecklistResult> Checklist { get; set; } = [];
    public string? Notes { get; set; }
    public CleaningStatus Status { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public string? VerifiedByName { get; private set; }
    public string? VerificationRemarks { get; private set; }

    public Result Verify(Guid? verifier, string verifierName, bool passed, string? remarks, DateTimeOffset now)
    {
        if (Status != CleaningStatus.AwaitingVerification)
        {
            return Error.Rule("housekeeping.already_verified", "This cleaning has already been checked.");
        }

        var allowed = CleaningRules.Verifier(CleanedBy, verifier);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (!passed && string.IsNullOrWhiteSpace(remarks))
        {
            return Error.Field("remarks", "Say what was not clean, so it is done again properly.");
        }

        Status = passed ? CleaningStatus.Verified : CleaningStatus.Redo;
        VerifiedAt = now;
        VerifiedBy = verifier;
        VerifiedByName = verifierName;
        VerificationRemarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        return Result.Success();
    }
}

public enum CleaningRequestKind
{
    /// <summary>Blood, body fluid, chemical or mercury spilled: cleaned within 15 minutes.</summary>
    Spill = 0,

    /// <summary>A room or bed after discharge, death or isolation, cleaned and disinfected top to bottom: within an hour.</summary>
    Terminal,

    /// <summary>Anything else a ward asks for: within two hours.</summary>
    Routine,
}

public enum CleaningRequestStatus
{
    Open = 0,
    Acknowledged,
    AwaitingVerification,
    Verified,
    Redo,
    Cancelled,
}

/// <summary>
/// A ward asks housekeeping to clean: a spill, a terminal clean, or anything else. The response time (asked to someone
/// on the way) is measured against the target for its kind; the cleaning is done against the kind's checklist and
/// verified by a supervisor who did not do it.
/// </summary>
public sealed class CleaningRequest : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public CleaningRequestKind Kind { get; set; }
    public Guid? WardId { get; set; }
    public string Location { get; set; } = string.Empty;
    public string? Details { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid? RequestedBy { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public CleaningRequestStatus Status { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public Guid? AcknowledgedBy { get; private set; }
    public string? AcknowledgedByName { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid? CompletedBy { get; private set; }
    public string? CompletedByName { get; private set; }
    public List<ChecklistResult> Checklist { get; set; } = [];
    public string? Notes { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public Guid? VerifiedBy { get; private set; }
    public string? VerifiedByName { get; private set; }
    public string? VerificationRemarks { get; private set; }

    /// <summary>The time within which someone is on the way.</summary>
    public static TimeSpan Target(CleaningRequestKind kind) => kind switch
    {
        CleaningRequestKind.Spill => TimeSpan.FromMinutes(15),
        CleaningRequestKind.Terminal => TimeSpan.FromMinutes(60),
        _ => TimeSpan.FromMinutes(120),
    };

    /// <summary>The checklist each kind of cleaning is done against.</summary>
    public static IReadOnlyList<string> ChecklistFor(CleaningRequestKind kind) => kind switch
    {
        CleaningRequestKind.Spill =>
        [
            "Area cordoned off",
            "Gloves, apron and mask worn",
            "Spill absorbed with the spill kit",
            "Area disinfected with 1% sodium hypochlorite",
            "Waste put in the yellow bag",
        ],
        CleaningRequestKind.Terminal =>
        [
            "Linen and curtains removed to the laundry",
            "Surfaces cleaned top to bottom",
            "Bed, mattress and rails disinfected",
            "Equipment wiped and disinfected",
            "Floor mopped with disinfectant",
            "Waste and sharps removed",
        ],
        _ => ["Area cleaned", "Waste removed"],
    };

    /// <summary>Minutes from the request to someone on the way, once acknowledged.</summary>
    public int? ResponseMinutes => AcknowledgedAt is { } at ? (int)Math.Round((at - RequestedAt).TotalMinutes) : null;

    /// <summary>Minutes from the request to the cleaning done.</summary>
    public int? CompletionMinutes => CompletedAt is { } at ? (int)Math.Round((at - RequestedAt).TotalMinutes) : null;

    /// <summary>Whether the response was, or is now, beyond the target for its kind.</summary>
    public bool ResponseLate(DateTimeOffset now) =>
        Status != CleaningRequestStatus.Cancelled && ((AcknowledgedAt ?? (Status == CleaningRequestStatus.Open ? now : RequestedAt)) - RequestedAt) > Target(Kind);

    public Result Acknowledge(Guid? by, string byName, DateTimeOffset now)
    {
        if (Status != CleaningRequestStatus.Open)
        {
            return Error.Rule("housekeeping.not_open", "Someone is already on this request, or it is closed.");
        }

        Status = CleaningRequestStatus.Acknowledged;
        AcknowledgedAt = now;
        AcknowledgedBy = by;
        AcknowledgedByName = byName;
        return Result.Success();
    }

    public Result Complete(IReadOnlyList<ChecklistResult> checklist, string? notes, Guid? by, string byName, DateTimeOffset now)
    {
        if (Status is not (CleaningRequestStatus.Open or CleaningRequestStatus.Acknowledged or CleaningRequestStatus.Redo))
        {
            return Error.Rule("housekeeping.not_open", "This request is already done or closed.");
        }

        var answered = CleaningRules.CheckList(ChecklistFor(Kind), checklist);
        if (answered.IsFailure)
        {
            return answered;
        }

        if (Status == CleaningRequestStatus.Open)
        {
            AcknowledgedAt = now;
            AcknowledgedBy = by;
            AcknowledgedByName = byName;
        }

        Status = CleaningRequestStatus.AwaitingVerification;
        Checklist = [.. checklist];
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        CompletedAt = now;
        CompletedBy = by;
        CompletedByName = byName;
        VerifiedAt = null;
        VerifiedBy = null;
        VerifiedByName = null;
        return Result.Success();
    }

    public Result Verify(Guid? verifier, string verifierName, bool passed, string? remarks, DateTimeOffset now)
    {
        if (Status != CleaningRequestStatus.AwaitingVerification)
        {
            return Error.Rule("housekeeping.not_done", "Only a cleaning marked done can be checked.");
        }

        var allowed = CleaningRules.Verifier(CompletedBy, verifier);
        if (allowed.IsFailure)
        {
            return allowed;
        }

        if (!passed && string.IsNullOrWhiteSpace(remarks))
        {
            return Error.Field("remarks", "Say what was not clean, so it is done again properly.");
        }

        Status = passed ? CleaningRequestStatus.Verified : CleaningRequestStatus.Redo;
        VerifiedAt = now;
        VerifiedBy = verifier;
        VerifiedByName = verifierName;
        VerificationRemarks = string.IsNullOrWhiteSpace(remarks) ? null : remarks.Trim();
        return Result.Success();
    }

    public Result Cancel(string reason)
    {
        if (Status is not (CleaningRequestStatus.Open or CleaningRequestStatus.Acknowledged))
        {
            return Error.Rule("housekeeping.not_open", "Only a request not yet done can be cancelled.");
        }

        Status = CleaningRequestStatus.Cancelled;
        Notes = reason.Trim();
        return Result.Success();
    }
}
