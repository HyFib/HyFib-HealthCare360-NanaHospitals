using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.CarePlan;

public sealed record CarePlanEvaluationDto(Guid Id, DateOnly DutyDate, NursingShift Shift, CarePlanProgress Progress, string? Note, DateTimeOffset EvaluatedAt, string? EvaluatedByName);

/// <param name="EvaluatedThisShift">An evaluation is recorded for the current duty date and shift.</param>
public sealed record CarePlanProblemDto(
    Guid Id,
    Guid AdmissionId,
    string ProblemCode,
    string Title,
    string Goal,
    IReadOnlyList<CarePlanIntervention> Interventions,
    CarePlanStatus Status,
    DateTimeOffset StartedAt,
    string? StartedByName,
    DateTimeOffset? ResolvedAt,
    string? ResolvedByName,
    string? ResolutionNote,
    CarePlanEvaluationDto? LastEvaluation,
    bool EvaluatedThisShift,
    uint Version);

/// <summary>
/// A problem from the list (<see cref="CarePlanProblems"/>), or <c>OTHER</c> with a <see cref="Title"/>. The goal and
/// interventions default to the list's; a problem written as "Other" names its own.
/// </summary>
public sealed record AddCarePlanProblemRequest(string ProblemCode, string? Title, string? Goal, IReadOnlyList<CarePlanIntervention>? Interventions);

public sealed record EvaluateCarePlanProblemRequest(CarePlanProgress Progress, string? Note);

public sealed record ResolveCarePlanProblemRequest(string Note, uint Version);

public sealed class AddCarePlanProblemRequestValidator : AbstractValidator<AddCarePlanProblemRequest>
{
    public AddCarePlanProblemRequestValidator()
    {
        RuleFor(x => x.ProblemCode).NotEmpty()
            .Must(code => code == CarePlanProblems.Other || CarePlanProblems.Find(code) is not null)
            .WithMessage("Choose a problem from the list, or Other.");
        RuleFor(x => x.Title).NotEmpty().When(x => x.ProblemCode == CarePlanProblems.Other).WithMessage("Name the problem.");
        RuleFor(x => x.Title).SafeText(200);
        RuleFor(x => x.Goal).NotEmpty().When(x => x.ProblemCode == CarePlanProblems.Other).WithMessage("Write the goal.");
        RuleFor(x => x.Goal).SafeText(500);
        RuleFor(x => x.Interventions).NotEmpty().When(x => x.ProblemCode == CarePlanProblems.Other).WithMessage("Add at least one intervention.");
        RuleFor(x => x.Interventions!.Count).LessThanOrEqualTo(12).When(x => x.Interventions is not null).WithName("interventions");
        RuleForEach(x => x.Interventions).ChildRules(i =>
        {
            i.RuleFor(x => x.Text).NotEmpty().SafeText(300);
            i.RuleFor(x => x.Frequency).NotEmpty().SafeText(100);
        });
    }
}

public sealed class EvaluateCarePlanProblemRequestValidator : AbstractValidator<EvaluateCarePlanProblemRequest>
{
    public EvaluateCarePlanProblemRequestValidator()
    {
        RuleFor(x => x.Progress).IsInEnum();
        RuleFor(x => x.Note).SafeText(500);
    }
}

public sealed class ResolveCarePlanProblemRequestValidator : AbstractValidator<ResolveCarePlanProblemRequest>
{
    public ResolveCarePlanProblemRequestValidator() => RuleFor(x => x.Note).NotEmpty().SafeText(500);
}
