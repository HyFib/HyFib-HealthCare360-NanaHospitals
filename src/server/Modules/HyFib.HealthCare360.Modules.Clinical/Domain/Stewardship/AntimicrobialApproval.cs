namespace HyFib.HealthCare360.Modules.Clinical.Domain.Stewardship;

public enum ApprovalStatus
{
    Pending = 0,
    Approved,
    Rejected,
}

/// <summary>What the review of a restricted antibiotic decided, at 72 hours or when the culture came back.</summary>
public enum StewardshipDecision
{
    Continue = 0,
    DeEscalate,
    Escalate,
    SwitchToOral,
    Stop,
}

public enum InfectionSite
{
    Respiratory = 0,
    UrinaryTract,
    Bloodstream,
    IntraAbdominal,
    SkinAndSoftTissue,
    BoneAndJoint,
    CentralNervousSystem,
    SurgicalSite,
    FebrileNeutropenia,
    Other,
}

/// <summary>
/// A restricted antibiotic on a signed prescription, waiting for (or given) the stewardship team's approval. The first
/// doses may be given while it waits, for at most <see cref="ApprovalWindow"/>; then the pharmacy issues no more until
/// it is approved, and never once it is rejected. An approved course is reviewed at <see cref="ReviewAt"/> (the
/// antibiotic time-out) or as soon as a culture result is released, whichever comes first, to continue, narrow, switch
/// to oral or stop it.
/// </summary>
public sealed class AntimicrobialApproval : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan ApprovalWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan ReviewAt = TimeSpan.FromHours(72);

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid PrescriptionId { get; set; }
    public string PrescriptionNumber { get; set; } = string.Empty;
    public Guid PrescriptionLineId { get; set; }
    public Guid? AdmissionId { get; set; }
    public string DrugName { get; set; } = string.Empty;

    /// <summary>The antibiotic as the AWaRe list names it.</summary>
    public string Antimicrobial { get; set; } = string.Empty;

    public AwareGroup Group { get; set; }
    public string? Dose { get; set; }
    public string? Route { get; set; }
    public int DurationDays { get; set; }
    public Guid? PrescribedByUserId { get; set; }
    public string PrescribedByName { get; set; } = string.Empty;
    public DateTimeOffset PrescribedAt { get; set; }

    // The prescriber's justification.
    public InfectionSite? Site { get; private set; }
    public string? Indication { get; private set; }
    public bool? Empirical { get; private set; }
    public bool? CultureSent { get; private set; }

    public ApprovalStatus Status { get; private set; }
    public string? DecidedByName { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public int? ApprovedDays { get; private set; }
    public string? DecisionNotes { get; private set; }

    // The culture that came back, and this antibiotic's result on its antibiogram.
    public Guid? CultureResultId { get; private set; }
    public string? Organism { get; private set; }
    public string? Susceptibility { get; private set; }
    public DateTimeOffset? CultureReleasedAt { get; private set; }

    public StewardshipDecision? ReviewDecision { get; private set; }
    public string? ReviewedByName { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? ReviewNotes { get; private set; }

    public DateTimeOffset ApprovalDueBy => PrescribedAt + ApprovalWindow;

    public bool ApprovalOverdue(DateTimeOffset now) => Status == ApprovalStatus.Pending && now > ApprovalDueBy;

    /// <summary>The review is due at 72 hours, or once a culture result is in, unless the course was rejected or already reviewed.</summary>
    public bool ReviewDue(DateTimeOffset now) =>
        Status != ApprovalStatus.Rejected && ReviewDecision is null && (CultureReleasedAt is not null || now >= PrescribedAt + ReviewAt);

    /// <summary>Why the pharmacy may not issue the antibiotic now; null when it may.</summary>
    public string? IssueRefusal(DateTimeOffset now) => Status switch
    {
        ApprovalStatus.Rejected => $"{DrugName} was not approved by antimicrobial stewardship{(DecisionNotes is null ? "" : $": {DecisionNotes}")}.",
        ApprovalStatus.Pending when now > ApprovalDueBy =>
            $"{DrugName} is a restricted antibiotic and has waited more than 24 hours for stewardship approval; only first doses go without it.",
        _ => null,
    };

    public Result Justify(InfectionSite site, string indication, bool empirical, bool cultureSent)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return Error.Rule("ams.decided", "The stewardship team has already decided on this antibiotic.");
        }

        if (string.IsNullOrWhiteSpace(indication))
        {
            return Error.Field("indication", "State the indication: the infection treated or suspected.");
        }

        Site = site;
        Indication = indication.Trim();
        Empirical = empirical;
        CultureSent = cultureSent;
        return Result.Success();
    }

    /// <summary>The stewardship team's decision; the prescriber cannot approve their own antibiotic.</summary>
    public Result Decide(bool approve, int? days, string? notes, Guid? byUserId, string byName, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return Error.Rule("ams.decided", "The stewardship team has already decided on this antibiotic.");
        }

        if (byUserId is not null && byUserId == PrescribedByUserId)
        {
            return Error.Rule("ams.own_prescription", "Another member of the stewardship team decides on an antibiotic you prescribed.");
        }

        if (approve && days is not (>= 1 and <= 42))
        {
            return Error.Field("days", "Approve for 1 to 42 days.");
        }

        if (!approve && string.IsNullOrWhiteSpace(notes))
        {
            return Error.Field("notes", "Say why it is not approved, and what to give instead.");
        }

        Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        ApprovedDays = approve ? days : null;
        DecisionNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        DecidedByName = byName;
        DecidedAt = now;
        return Result.Success();
    }

    /// <summary>A culture result released for the patient while the course runs: the review falls due at once.</summary>
    public void CultureReleased(Guid resultId, string organism, string? susceptibility, DateTimeOffset at)
    {
        if (ReviewDecision is not null || Status == ApprovalStatus.Rejected)
        {
            return;
        }

        CultureResultId = resultId;
        Organism = organism;
        Susceptibility = susceptibility;
        CultureReleasedAt = at;
    }

    public Result Review(StewardshipDecision decision, string? notes, string byName, DateTimeOffset now)
    {
        if (Status == ApprovalStatus.Rejected)
        {
            return Error.Rule("ams.rejected", "A rejected antibiotic has no course to review.");
        }

        if (ReviewDecision is not null)
        {
            return Error.Rule("ams.reviewed", "This course has already been reviewed.");
        }

        if (!ReviewDue(now))
        {
            return Error.Rule("ams.review_not_due", "The review is due 72 hours after prescribing, or when a culture result is released.");
        }

        if (decision == StewardshipDecision.Continue && Susceptibility == "Resistant")
        {
            if (string.IsNullOrWhiteSpace(notes))
            {
                return Error.Field("notes", $"The {Organism} isolate is resistant to {Antimicrobial}. Say why it continues.");
            }
        }

        if (decision != StewardshipDecision.Continue && string.IsNullOrWhiteSpace(notes))
        {
            return Error.Field("notes", "Name the antibiotic the patient goes on to, or why it stops.");
        }

        ReviewDecision = decision;
        ReviewNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ReviewedByName = byName;
        ReviewedAt = now;
        return Result.Success();
    }
}
