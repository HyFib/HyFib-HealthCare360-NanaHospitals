namespace HyFib.HealthCare360.Modules.Pharmacy.Domain;

/// <summary>Why a reaction is serious (PvPI suspected ADR reporting form, item 12); none of them is a non-serious reaction.</summary>
[Flags]
public enum AdrSeriousness
{
    None = 0,
    Death = 1,
    LifeThreatening = 2,
    Hospitalisation = 4,
    Disability = 8,
    CongenitalAnomaly = 16,
    RequiredIntervention = 32,
    OtherMedicallyImportant = 64,
}

public enum AdrOutcome
{
    Unknown = 0,
    Recovered,
    Recovering,
    NotRecovered,
    RecoveredWithSequelae,
    Fatal,
}

/// <summary>Did the reaction abate when the drug was stopped (dechallenge), or come back when it was given again (rechallenge)?</summary>
public enum ChallengeResult
{
    NotApplicable = 0,
    Yes,
    No,
    Unknown,
}

public enum AdrActionTaken
{
    Unknown = 0,
    Withdrawn,
    DoseReduced,
    DoseIncreased,
    NotChanged,
}

/// <summary>The WHO-UMC causality categories the ADR monitoring centre assigns.</summary>
public enum WhoUmcCausality
{
    Certain = 0,
    Probable,
    Possible,
    Unlikely,
    ConditionalUnclassified,
    UnassessableUnclassifiable,
}

public enum AdrReportStatus
{
    /// <summary>Being written, by whoever saw the reaction.</summary>
    Draft = 0,

    /// <summary>Assessed and sent to the ADR monitoring centre (AMC) or through the PvPI channel.</summary>
    Submitted,
}

/// <summary>A drug given before the reaction: the suspected one(s), and the others the patient was taking (concomitant).</summary>
public sealed record AdrDrug(
    string Name,
    string? Manufacturer,
    string? BatchNumber,
    DateOnly? ExpiryDate,
    string? Dose,
    string? Route,
    string? Frequency,
    DateOnly? StartedOn,
    DateOnly? StoppedOn,
    string? Indication,
    bool Suspected,
    AdrActionTaken Action,
    ChallengeResult Dechallenge,
    ChallengeResult Rechallenge);

/// <summary>The reaction as the reporter describes it.</summary>
public sealed record AdrDetails(
    string Reaction,
    DateOnly ReactionStartedOn,
    DateOnly? ReactionStoppedOn,
    AdrSeriousness Seriousness,
    AdrOutcome Outcome,
    decimal? WeightKg,
    string? RelevantTests,
    string? MedicalHistory,
    IReadOnlyList<AdrDrug> Drugs);

/// <summary>
/// A suspected adverse drug reaction, reported on the Pharmacovigilance Programme of India's form (IPC, PvPI): the
/// patient, the reaction, the suspected and concomitant drugs with their dechallenge and rechallenge, seriousness and
/// outcome, and the reporter. Pharmacovigilance assesses causality (WHO-UMC) before it is sent to the ADR monitoring
/// centre; a serious reaction is due within <see cref="SeriousDays"/> days of the hospital learning of it.
/// </summary>
public sealed class AdrReport : AuditableEntity, IFacilityScoped
{
    /// <summary>A serious reaction is sent within this many days of the hospital learning of it (PvPI guidance).</summary>
    public const int SeriousDays = 15;

    public Guid FacilityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public Guid PatientId { get; set; }

    /// <summary>The incident report this reaction was also reported as, if any (Quality's number).</summary>
    public string? IncidentNumber { get; set; }

    public AdrReportStatus Status { get; private set; }
    public DateOnly LearnedOn { get; set; }

    public string Reaction { get; private set; } = string.Empty;
    public DateOnly ReactionStartedOn { get; private set; }
    public DateOnly? ReactionStoppedOn { get; private set; }
    public AdrSeriousness Seriousness { get; private set; }
    public AdrOutcome Outcome { get; private set; }
    public decimal? WeightKg { get; private set; }
    public string? RelevantTests { get; private set; }
    public string? MedicalHistory { get; private set; }
    public List<AdrDrug> Drugs { get; private set; } = [];

    public Guid? ReportedBy { get; set; }
    public string ReporterName { get; set; } = string.Empty;
    public string? ReporterDesignation { get; set; }

    public WhoUmcCausality? Causality { get; private set; }
    public string? AssessedByName { get; private set; }
    public DateOnly? SubmittedOn { get; private set; }

    /// <summary>The AMC's or VigiFlow's reference for the report.</summary>
    public string? SubmissionReference { get; private set; }

    public bool IsSerious => Seriousness != AdrSeriousness.None;

    public DateOnly? DueBy => IsSerious ? LearnedOn.AddDays(SeriousDays) : null;

    public bool IsOverdue(DateOnly today) => Status == AdrReportStatus.Draft && DueBy is { } due && today > due;

    public Result Record(AdrDetails details, DateOnly today)
    {
        if (Status == AdrReportStatus.Submitted)
        {
            return Error.Rule("adr.submitted", "The report has been sent; a follow-up is a new report that names this one.");
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(details.Reaction))
        {
            errors["reaction"] = ["Describe the reaction."];
        }

        if (details.ReactionStartedOn > today)
        {
            errors["reactionStartedOn"] = ["The reaction cannot start in the future."];
        }

        if (details.ReactionStoppedOn is { } stopped && (stopped < details.ReactionStartedOn || stopped > today))
        {
            errors["reactionStoppedOn"] = ["The reaction stops on or after it starts, and not in the future."];
        }

        if (!details.Drugs.Any(d => d.Suspected))
        {
            errors["drugs"] = ["Name at least one suspected drug."];
        }

        if (details.Drugs.Any(d => d.StartedOn is { } start && d.StoppedOn is { } stop && stop < start))
        {
            errors["drugs"] = ["A drug is stopped on or after it is started."];
        }

        if (details.Outcome == AdrOutcome.Fatal != details.Seriousness.HasFlag(AdrSeriousness.Death))
        {
            errors["outcome"] = ["A fatal outcome and death as the seriousness go together."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("adr.invalid", "Some details of the report are missing or wrong.", errors);
        }

        Reaction = details.Reaction.Trim();
        ReactionStartedOn = details.ReactionStartedOn;
        ReactionStoppedOn = details.ReactionStoppedOn;
        Seriousness = details.Seriousness;
        Outcome = details.Outcome;
        WeightKg = details.WeightKg;
        RelevantTests = Clean(details.RelevantTests);
        MedicalHistory = Clean(details.MedicalHistory);
        Drugs = [.. details.Drugs];
        return Result.Success();
    }

    /// <summary>Pharmacovigilance assesses causality and sends the report; it cannot change after.</summary>
    public Result Submit(WhoUmcCausality causality, string assessedBy, DateOnly submittedOn, string? reference, DateOnly today)
    {
        if (Status == AdrReportStatus.Submitted)
        {
            return Error.Rule("adr.submitted", "The report has already been sent.");
        }

        if (submittedOn > today || submittedOn < LearnedOn)
        {
            return Error.Field("submittedOn", "Give the date it was sent: on or after the hospital learned of the reaction, and not in the future.");
        }

        Causality = causality;
        AssessedByName = assessedBy;
        SubmittedOn = submittedOn;
        SubmissionReference = Clean(reference);
        Status = AdrReportStatus.Submitted;
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
