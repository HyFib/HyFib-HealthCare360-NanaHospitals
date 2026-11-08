namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// Adverse transfusion reaction report (BB-07, haemovigilance). Reported from the ward by the treating team,
/// investigated by the blood bank, classified per the Haemovigilance Programme of India and then closed.
/// </summary>
public sealed class TransfusionReaction : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid IssueId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ComponentId { get; set; }
    public Guid PatientId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;

    public DateTimeOffset ReportedAt { get; set; }
    public string? ReportedByName { get; set; }
    public DateTimeOffset OnsetAt { get; set; }
    public List<ReactionSymptom> Symptoms { get; set; } = [];

    /// <summary>Vital signs at the time of the reaction (free text as charted: "T 39.2, P 124, BP 86/50").</summary>
    public string? Vitals { get; set; }

    public bool TransfusionStopped { get; set; }
    public int? VolumeTransfusedMl { get; set; }
    public string? ActionsTaken { get; set; }
    public ReactionSeverity Severity { get; set; }

    public ReactionType Type { get; set; }

    /// <summary>The blood bank's assessment of whether the transfusion caused it (<see cref="ReactionDowngrade"/>).</summary>
    public ReactionImputability Imputability { get; set; }

    /// <summary>Who lowered the severity or imputability, and who asked when the medical officer approved it.</summary>
    public string? LoweredByName { get; set; }

    public string? LowerRequestedByName { get; set; }
    public string? LowerReason { get; set; }
    public ReactionStatus Status { get; set; }
    public string? InvestigationFindings { get; set; }
    public bool ReportedToHaemovigilance { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public string? ClosedByName { get; set; }

    /// <summary>Severe reactions are escalated immediately to blood bank officers.</summary>
    public bool IsSerious => Severity >= ReactionSeverity.Severe;

    public Result StartInvestigation()
    {
        if (Status == ReactionStatus.Closed)
        {
            return Error.Rule("bloodbank.reaction_closed", "This reaction report is closed.");
        }

        Status = ReactionStatus.UnderInvestigation;
        return Result.Success();
    }

    /// <summary>
    /// Records the blood bank's classification while the report is open. Lowering the severity or imputability is made only
    /// when the medical officer approved it (or the hospital lets the officer do it alone): the service checks
    /// <see cref="ReactionDowngrade.Lowers"/> before calling this.
    /// </summary>
    public Result Classify(ReactionType type, ReactionSeverity severity, ReactionImputability imputability)
    {
        if (Status == ReactionStatus.Closed)
        {
            return Error.Rule("bloodbank.reaction_closed", "This reaction report is closed.");
        }

        Type = type;
        Severity = severity;
        Imputability = imputability;
        if (Status == ReactionStatus.Reported)
        {
            Status = ReactionStatus.UnderInvestigation;
        }

        return Result.Success();
    }

    /// <summary>The names kept when a classification was lowered.</summary>
    public void MarkLowered(string reason, string loweredBy, string? requestedBy)
    {
        LowerReason = reason.Trim();
        LoweredByName = loweredBy;
        LowerRequestedByName = requestedBy;
    }

    public Result Close(
        ReactionType type, ReactionSeverity severity, ReactionImputability imputability, string findings, bool reportedToHaemovigilance, string closedBy, DateTimeOffset now)
    {
        if (Status == ReactionStatus.Closed)
        {
            return Error.Rule("bloodbank.reaction_closed", "This reaction report is already closed.");
        }

        if (type == ReactionType.Unclassified)
        {
            return Error.Field("type", "Classify the reaction before closing the investigation.");
        }

        if (string.IsNullOrWhiteSpace(findings))
        {
            return Error.Field("findings", "Record the investigation findings.");
        }

        if (imputability == ReactionImputability.NotAssessed)
        {
            return Error.Field("imputability", "Assess the imputability before closing the investigation.");
        }

        Type = type;
        Severity = severity;
        Imputability = imputability;
        InvestigationFindings = findings.Trim();
        ReportedToHaemovigilance = reportedToHaemovigilance;
        Status = ReactionStatus.Closed;
        ClosedAt = now;
        ClosedByName = closedBy;
        return Result.Success();
    }
}
