namespace HyFib.HealthCare360.Modules.Quality.Domain;

public enum RcaMethod
{
    FiveWhys = 0,

    /// <summary>Ishikawa (cause-and-effect) diagram grouped by the contributing-factor categories.</summary>
    Fishbone,
}

public enum ContributingFactorCategory
{
    People = 0,
    Process,
    Equipment,
    Environment,
    Communication,
    Policy,
}

public enum InvestigationStatus
{
    InProgress = 0,
    Completed,
}

/// <summary>
/// Root-cause analysis of an incident (IRMS-03). A completed RCA must name at least one root cause and enough analysis
/// for its method (three "whys" or two fishbone factors), because a sentinel event cannot be closed without one.
/// </summary>
public sealed class Investigation : AuditableEntity, IFacilityScoped
{
    public const int MinimumWhys = 3;
    public const int MinimumFishboneFactors = 2;

    public Guid FacilityId { get; set; }
    public Guid IncidentId { get; set; }
    public RcaMethod Method { get; set; }
    public string ProblemStatement { get; set; } = string.Empty;

    /// <summary>Successive "why?" answers for the five-whys method.</summary>
    public List<string> Whys { get; set; } = [];

    public string? Findings { get; set; }
    public string? Recommendations { get; set; }
    public List<Guid> InvestigatorUserIds { get; set; } = [];
    public InvestigationStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public List<RcaFactor> Factors { get; set; } = [];

    public bool IsCompleted => Status == InvestigationStatus.Completed;

    public Result Complete(Guid? userId, DateTimeOffset now)
    {
        if (IsCompleted)
        {
            return Error.Rule("rca.completed", "The analysis is already complete.");
        }

        if (string.IsNullOrWhiteSpace(ProblemStatement) || string.IsNullOrWhiteSpace(Findings))
        {
            return Error.Rule("rca.incomplete", "Record the problem statement and the findings.");
        }

        if (!Factors.Any(f => f.IsRootCause))
        {
            return Error.Rule("rca.no_root_cause", "Identify at least one root cause.");
        }

        if (Method == RcaMethod.FiveWhys && Whys.Count(w => !string.IsNullOrWhiteSpace(w)) < MinimumWhys)
        {
            return Error.Rule("rca.whys", $"Ask 'why?' at least {MinimumWhys} times before concluding.");
        }

        if (Method == RcaMethod.Fishbone && Factors.Count < MinimumFishboneFactors)
        {
            return Error.Rule("rca.factors", $"Record at least {MinimumFishboneFactors} contributing factors on the fishbone.");
        }

        Status = InvestigationStatus.Completed;
        CompletedAt = now;
        CompletedByUserId = userId;
        return Result.Success();
    }

    /// <summary>Re-opens a completed analysis (new evidence, or the incident was reopened).</summary>
    public Result Reopen()
    {
        if (!IsCompleted)
        {
            return Error.Rule("rca.not_completed", "The analysis is still in progress.");
        }

        Status = InvestigationStatus.InProgress;
        CompletedAt = null;
        CompletedByUserId = null;
        return Result.Success();
    }
}

/// <summary>A contributing factor (fishbone bone); root causes are the factors marked as such.</summary>
public sealed class RcaFactor : Entity
{
    public Guid InvestigationId { get; set; }
    public ContributingFactorCategory Category { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsRootCause { get; set; }
}
