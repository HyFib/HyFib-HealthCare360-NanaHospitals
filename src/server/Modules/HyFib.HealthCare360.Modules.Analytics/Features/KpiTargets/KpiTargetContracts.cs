using HyFib.HealthCare360.Modules.Analytics.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Features.KpiTargets;

/// <summary>A KPI's target as stored for the facility (monthly for counts and money).</summary>
public sealed record KpiTargetDto(
    decimal Target,
    decimal AmberThreshold,
    decimal RedThreshold,
    string OwnerRole,
    string? SetByName,
    DateTimeOffset SetAt,
    KpiStatus? LastStatus,
    decimal? LastValue,
    DateTimeOffset? LastCheckedAt,
    uint Version);

/// <summary>One KPI of the catalogue with the facility's target, if one is set.</summary>
/// <param name="Monthly">True for counts and money: the target is a monthly figure, prorated to the period shown.</param>
public sealed record KpiTargetItem(string Key, string Label, string Unit, bool HigherIsBetter, bool Monthly, string Formula, KpiTargetDto? Target);

/// <summary>GET /api/v1/analytics/kpi-targets — every KPI with the signed-in facility's targets, and the roles that can own one.</summary>
public sealed record KpiTargetsDto(string FacilityName, IReadOnlyList<string> Roles, IReadOnlyList<KpiTargetItem> Kpis);

/// <summary>PUT /api/v1/analytics/kpi-targets/{key}: sets the facility's target (no version on the first save).</summary>
public sealed record SaveKpiTargetRequest(decimal Target, decimal AmberThreshold, decimal RedThreshold, string OwnerRole, uint? Version = null);

public sealed class SaveKpiTargetRequestValidator : AbstractValidator<SaveKpiTargetRequest>
{
    /// <summary>Large enough for a year's revenue of a big hospital in rupees, small enough to stay a sane figure.</summary>
    public const decimal Largest = 100_000_000_000m;

    public SaveKpiTargetRequestValidator()
    {
        RuleFor(x => x.Target).InclusiveBetween(-100m, Largest).WithMessage("Enter a target between -100 and 1,00,00,00,00,000.");
        RuleFor(x => x.AmberThreshold).InclusiveBetween(-100m, Largest).WithMessage("Enter an amber threshold between -100 and 1,00,00,00,00,000.");
        RuleFor(x => x.RedThreshold).InclusiveBetween(-100m, Largest).WithMessage("Enter a red threshold between -100 and 1,00,00,00,00,000.");
        RuleFor(x => x.OwnerRole).NotEmpty().WithMessage("Choose the role told when the KPI is missed.").SafeText(KpiTarget.MaxRoleLength);
    }
}
