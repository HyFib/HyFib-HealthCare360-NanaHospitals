using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.CarePlan;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;

public sealed record NursingNoteDto(
    Guid Id,
    DateOnly DutyDate,
    NursingShift Shift,
    NursingNoteCategory Category,
    string Note,
    DateTimeOffset NoteFor,
    DateTimeOffset RecordedAt,
    string? RecordedByName,
    bool IsLateEntry);

public sealed record AddNursingNoteRequest(NursingNoteCategory Category, string Note, DateTimeOffset? NoteFor);

public sealed record HandoverDto(
    Guid Id,
    Guid AdmissionId,
    Guid PatientId,
    Guid? WardId,
    DateOnly DutyDate,
    NursingShift FromShift,
    string Situation,
    string? Background,
    string? Assessment,
    string? Recommendation,
    string? PendingTasks,
    DateTimeOffset HandedOverAt,
    string? HandedOverByName,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedByName);

/// <param name="CarePlan">The stay's active care-plan problems, with their last evaluation, for the receiving shift.</param>
public sealed record WardHandoverItem(HandoverDto Handover, string Uhid, string PatientName, string? BedLabel, IReadOnlyList<CarePlanProblemDto>? CarePlan = null);

public sealed record AddHandoverRequest(string Situation, string? Background, string? Assessment, string? Recommendation, string? PendingTasks);

public sealed record IntakeOutputDto(
    Guid Id,
    DateTimeOffset RecordedAt,
    NursingShift Shift,
    FluidDirection Direction,
    FluidRoute Route,
    int VolumeMl,
    string? Description,
    string? Notes,
    string? RecordedByName);

public sealed record AddIntakeOutputRequest(FluidDirection Direction, FluidRoute Route, int VolumeMl, DateTimeOffset? RecordedAt, string? Description, string? Notes);

public sealed record VoidEntryRequest(string Reason);

public sealed record IntakeOutputChart(DateOnly DutyDate, FluidBalanceSummary Balance, IReadOnlyList<IntakeOutputDto> Entries);

public sealed record AssessmentDto(
    Guid Id,
    AssessmentType Type,
    int Score,
    RiskLevel Risk,
    string Interpretation,
    JsonNode? Responses,
    DateTimeOffset AssessedAt,
    string? AssessedByName,
    string? Notes);

public sealed record MorseAssessmentRequest(
    bool HistoryOfFalling,
    bool SecondaryDiagnosis,
    AmbulatoryAid AmbulatoryAid,
    bool IntravenousAccess,
    GaitType Gait,
    bool OverestimatesAbility,
    string? Notes);

public sealed record BradenAssessmentRequest(int SensoryPerception, int Moisture, int Activity, int Mobility, int Nutrition, int FrictionShear, string? Notes);

public sealed record PainAssessmentRequest(int Score, PainScaleKind Scale, string? Location, string? Character, string? Notes);

internal static class NursingRules
{
    // How far back an entry may be charted is the nursing chart's back-entry window (BackEntryWindows, 24 hours unless
    // the administrator sets otherwise, at most 14 days); older needs a late back-entry approval.
}

public sealed class AddNursingNoteRequestValidator : AbstractValidator<AddNursingNoteRequest>
{
    public AddNursingNoteRequestValidator(TimeProvider time, BackEntryService backEntry)
    {
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Note).NotEmpty().SafeText(4000);
        RuleFor(x => x.NoteFor).NotInFuture(time).WithMessage("A note cannot describe the future.")
            .WithinBackEntry(backEntry, BackEntryRecord.NursingChart);
    }
}

public sealed class AddHandoverRequestValidator : AbstractValidator<AddHandoverRequest>
{
    public AddHandoverRequestValidator()
    {
        RuleFor(x => x.Situation).NotEmpty().SafeText(2000);
        RuleFor(x => x.Background).SafeText(2000);
        RuleFor(x => x.Assessment).SafeText(2000);
        RuleFor(x => x.Recommendation).SafeText(2000);
        RuleFor(x => x.PendingTasks).SafeText(1000);
    }
}

public sealed class AddIntakeOutputRequestValidator : AbstractValidator<AddIntakeOutputRequest>
{
    public AddIntakeOutputRequestValidator(TimeProvider time, BackEntryService backEntry)
    {
        RuleFor(x => x.Direction).IsInEnum();
        RuleFor(x => x.Route).IsInEnum();
        RuleFor(x => x).Must(x => FluidBalance.RouteMatches(x.Direction, x.Route)).WithName("route")
            .WithMessage("This route does not match the direction (for example urine is an output).");
        RuleFor(x => x.VolumeMl).InclusiveBetween(1, 10_000).WithMessage("Enter a volume between 1 and 10,000 ml.");
        RuleFor(x => x.RecordedAt).NotInFuture(time).WithMessage("An entry cannot be made for the future.")
            .WithinBackEntry(backEntry, BackEntryRecord.NursingChart);
        RuleFor(x => x.Description).SafeText(200);
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class VoidEntryRequestValidator : AbstractValidator<VoidEntryRequest>
{
    public VoidEntryRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class MorseAssessmentRequestValidator : AbstractValidator<MorseAssessmentRequest>
{
    public MorseAssessmentRequestValidator()
    {
        RuleFor(x => x.AmbulatoryAid).IsInEnum();
        RuleFor(x => x.Gait).IsInEnum();
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class BradenAssessmentRequestValidator : AbstractValidator<BradenAssessmentRequest>
{
    public BradenAssessmentRequestValidator()
    {
        RuleFor(x => x.SensoryPerception).InclusiveBetween(1, 4);
        RuleFor(x => x.Moisture).InclusiveBetween(1, 4);
        RuleFor(x => x.Activity).InclusiveBetween(1, 4);
        RuleFor(x => x.Mobility).InclusiveBetween(1, 4);
        RuleFor(x => x.Nutrition).InclusiveBetween(1, 4);
        RuleFor(x => x.FrictionShear).InclusiveBetween(1, 3);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class PainAssessmentRequestValidator : AbstractValidator<PainAssessmentRequest>
{
    public PainAssessmentRequestValidator()
    {
        RuleFor(x => x.Scale).IsInEnum();
        RuleFor(x => x.Score).InclusiveBetween(0, 10);
        RuleFor(x => x.Score).Must(score => score % 2 == 0).When(x => x.Scale == PainScaleKind.WongBakerFaces)
            .WithMessage("Wong-Baker FACES scores are 0, 2, 4, 6, 8 or 10.");
        RuleFor(x => x.Location).MaximumLength(100);
        RuleFor(x => x.Character).MaximumLength(100);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}
