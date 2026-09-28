using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Features.Risks;

public sealed record RiskListItem(
    Guid Id,
    string RiskNumber,
    string Title,
    RiskCategory Category,
    Guid? DepartmentId,
    int Likelihood,
    int Impact,
    int Score,
    RiskRating Rating,
    int? ResidualScore,
    RiskRating? ResidualRating,
    RiskStatus Status,
    Guid? OwnerUserId,
    DateOnly ReviewDate,
    bool IsReviewOverdue);

public sealed record RiskDetail(
    Guid Id,
    string RiskNumber,
    string Title,
    string? Description,
    RiskCategory Category,
    Guid? DepartmentId,
    int Likelihood,
    int Impact,
    int Score,
    RiskRating Rating,
    int? ResidualLikelihood,
    int? ResidualImpact,
    int? ResidualScore,
    RiskRating? ResidualRating,
    string? ExistingControls,
    string? MitigationPlan,
    Guid? OwnerUserId,
    string? OwnerName,
    DateOnly ReviewDate,
    DateTimeOffset? LastReviewedAt,
    string? LastReviewNote,
    RiskStatus Status,
    bool IsReviewOverdue,
    uint Version);

public sealed record SaveRiskRequest(
    string Title,
    string? Description,
    RiskCategory Category,
    Guid? DepartmentId,
    int Likelihood,
    int Impact,
    int? ResidualLikelihood,
    int? ResidualImpact,
    string? ExistingControls,
    string? MitigationPlan,
    Guid? OwnerUserId,
    DateOnly ReviewDate,
    RiskStatus Status,
    uint? Version);

/// <summary>Periodic re-assessment: new scores, controls and the next review date.</summary>
public sealed record ReviewRiskRequest(
    int Likelihood,
    int Impact,
    int? ResidualLikelihood,
    int? ResidualImpact,
    string? ExistingControls,
    DateOnly NextReviewDate,
    string? Note,
    uint Version);

public sealed record RiskMatrixCell(int Likelihood, int Impact, int Score, RiskRating Rating, int Count);

public sealed class SaveRiskRequestValidator : AbstractValidator<SaveRiskRequest>
{
    public SaveRiskRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().SafeText(200);
        RuleFor(x => x.Description).SafeText(2000);
        RuleFor(x => x.Likelihood).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.Impact).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.ResidualLikelihood).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.ResidualImpact).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.ExistingControls).SafeText(2000);
        RuleFor(x => x.MitigationPlan).SafeText(2000);
    }
}

public sealed class ReviewRiskRequestValidator : AbstractValidator<ReviewRiskRequest>
{
    public ReviewRiskRequestValidator()
    {
        RuleFor(x => x.Likelihood).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.Impact).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.ResidualLikelihood).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.ResidualImpact).InclusiveBetween(RiskMatrix.MinLevel, RiskMatrix.MaxLevel);
        RuleFor(x => x.ExistingControls).SafeText(2000);
        RuleFor(x => x.Note).SafeText(1000);
    }
}
