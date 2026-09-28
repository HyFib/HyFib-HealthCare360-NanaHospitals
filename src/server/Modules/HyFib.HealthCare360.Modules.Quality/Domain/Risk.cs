namespace HyFib.HealthCare360.Modules.Quality.Domain;

public enum RiskCategory
{
    Clinical = 0,
    PatientSafety,
    InfectionControl,
    Operational,
    Financial,
    Compliance,
    FireAndSafety,
    Environmental,
    InformationSecurity,
    Reputational,
    Strategic,
}

public enum RiskRating
{
    Low = 0,
    Medium,
    High,
    Extreme,
}

public enum RiskStatus
{
    Open = 0,
    Mitigating,

    /// <summary>Within appetite: accepted by management with existing controls.</summary>
    Accepted,
    Closed,
}

/// <summary>
/// 5 × 5 risk matrix (IRMS-05): likelihood (1 rare … 5 almost certain) × impact (1 negligible … 5 catastrophic).
/// Scores 1–4 are Low, 5–9 Medium, 10–16 High and 20–25 Extreme.
/// </summary>
public static class RiskMatrix
{
    public const int MinLevel = 1;
    public const int MaxLevel = 5;

    public static bool IsValidLevel(int level) => level is >= MinLevel and <= MaxLevel;

    public static int Score(int likelihood, int impact)
    {
        if (!IsValidLevel(likelihood) || !IsValidLevel(impact))
        {
            throw new ArgumentOutOfRangeException(nameof(likelihood), "Likelihood and impact are rated 1 to 5.");
        }

        return likelihood * impact;
    }

    public static RiskRating Rate(int score) => score switch
    {
        <= 4 => RiskRating.Low,
        <= 9 => RiskRating.Medium,
        <= 16 => RiskRating.High,
        _ => RiskRating.Extreme,
    };

    public static RiskRating Rate(int likelihood, int impact) => Rate(Score(likelihood, impact));
}

/// <summary>A risk in the facility risk register with inherent and residual (after controls) scores.</summary>
public sealed class Risk : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}RSK{SEQ:4}".</summary>
    public string RiskNumber { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public RiskCategory Category { get; set; }
    public Guid? DepartmentId { get; set; }
    public int Likelihood { get; set; }
    public int Impact { get; set; }
    public int Score { get; set; }
    public RiskRating Rating { get; set; }
    public string? ExistingControls { get; set; }
    public string? MitigationPlan { get; set; }
    public Guid? OwnerUserId { get; set; }
    public DateOnly ReviewDate { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
    public string? LastReviewNote { get; set; }
    public int? ResidualLikelihood { get; set; }
    public int? ResidualImpact { get; set; }
    public int? ResidualScore { get; set; }
    public RiskRating? ResidualRating { get; set; }
    public RiskStatus Status { get; set; }

    public bool IsReviewOverdue(DateOnly today) => Status is not RiskStatus.Closed && ReviewDate < today;

    /// <summary>Scores the risk; residual risk (after controls) can never exceed the inherent risk.</summary>
    public Result Assess(int likelihood, int impact, int? residualLikelihood, int? residualImpact)
    {
        if (!RiskMatrix.IsValidLevel(likelihood) || !RiskMatrix.IsValidLevel(impact))
        {
            return Error.Field("likelihood", "Rate likelihood and impact from 1 to 5.");
        }

        if (residualLikelihood is null != residualImpact is null)
        {
            return Error.Field("residualLikelihood", "Rate both residual likelihood and residual impact, or neither.");
        }

        int? residualScore = null;
        if (residualLikelihood is { } rl && residualImpact is { } ri)
        {
            if (!RiskMatrix.IsValidLevel(rl) || !RiskMatrix.IsValidLevel(ri))
            {
                return Error.Field("residualLikelihood", "Rate residual likelihood and impact from 1 to 5.");
            }

            residualScore = RiskMatrix.Score(rl, ri);
            if (residualScore > RiskMatrix.Score(likelihood, impact))
            {
                return Error.Rule("risk.residual_above_inherent", "Residual risk cannot be higher than the risk before controls.");
            }
        }

        Likelihood = likelihood;
        Impact = impact;
        Score = RiskMatrix.Score(likelihood, impact);
        Rating = RiskMatrix.Rate(Score);
        ResidualLikelihood = residualLikelihood;
        ResidualImpact = residualImpact;
        ResidualScore = residualScore;
        ResidualRating = residualScore is { } s ? RiskMatrix.Rate(s) : null;
        return Result.Success();
    }

    public Result RecordReview(DateOnly nextReviewDate, string? note, DateOnly today, DateTimeOffset now)
    {
        if (nextReviewDate <= today)
        {
            return Error.Field("nextReviewDate", "The next review must be in the future.");
        }

        ReviewDate = nextReviewDate;
        LastReviewedAt = now;
        LastReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return Result.Success();
    }
}
