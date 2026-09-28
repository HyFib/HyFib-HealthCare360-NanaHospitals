using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Mar;

public sealed record MedicationOrderDto(
    Guid Id,
    Guid? PrescriptionId,
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string Dose,
    string? Route,
    string Frequency,
    int? DurationDays,
    DateTimeOffset StartAt,
    DateTimeOffset? EndAt,
    string? Instructions,
    bool IsControlledDrug,
    bool IsAsNeeded,
    MedicationOrderSource Source,
    MedicationOrderStatus Status,
    DateTimeOffset? LastDispensedAt,
    DateTimeOffset? StoppedAt,
    string? StopReason,
    IReadOnlyList<TimeOnly> RoundTimes);

public sealed record AddMedicationOrderRequest(
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string Dose,
    string? Route,
    string Frequency,
    int? DurationDays,
    DateTimeOffset? StartAt,
    string? Instructions,
    bool IsControlledDrug,
    Guid? OrderedByPractitionerId);

public sealed record StopMedicationOrderRequest(string Reason);

public sealed record MarEntryDto(
    Guid Id,
    Guid? MedicationOrderId,
    string DrugName,
    string Dose,
    string? Route,
    DateTimeOffset ScheduledAt,
    MarStatus Status,
    bool IsOverdue,
    bool IsAdHoc,
    DateTimeOffset? AdministeredAt,
    string? RecordedByName,
    string? Reason,
    string? BatchNumber,
    string? WitnessName,
    string? Notes,
    uint Version);

/// <summary>An SOS/STAT or otherwise unscheduled dose; <see cref="GivenNow"/> records it as given in the same step.</summary>
public sealed record AddAdHocDoseRequest(
    Guid? MedicationOrderId,
    string? DrugName,
    string Dose,
    string? Route,
    DateTimeOffset? ScheduledAt,
    bool GivenNow,
    string? Reason,
    string? BatchNumber,
    string? WitnessName,
    string? Notes);

public sealed record RecordAdministrationRequest(
    MarStatus Outcome,
    DateTimeOffset? AdministeredAt,
    string? Reason,
    string? BatchNumber,
    string? WitnessName,
    string? Notes,
    uint Version);

public sealed record WardMarItem(
    Guid EntryId,
    Guid AdmissionId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? BedLabel,
    string DrugName,
    string Dose,
    string? Route,
    DateTimeOffset ScheduledAt,
    bool IsOverdue,
    int MinutesLate,
    uint Version);

public sealed record ScheduleResult(Guid MedicationOrderId, int DosesAdded);

public sealed class AddMedicationOrderRequestValidator : AbstractValidator<AddMedicationOrderRequest>
{
    public AddMedicationOrderRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.DrugName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.GenericName).MaximumLength(200);
        RuleFor(x => x.Strength).MaximumLength(64);
        RuleFor(x => x.Dose).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Route).MaximumLength(32);
        RuleFor(x => x.Frequency).NotEmpty().MaximumLength(32);
        RuleFor(x => x.DurationDays).InclusiveBetween(1, 90);
        RuleFor(x => x.Instructions).SafeText(500);
        RuleFor(x => x.StartAt)
            .Must(at => at is null || (at >= time.GetUtcNow().AddHours(-24) && at <= time.GetUtcNow().AddDays(7)))
            .WithMessage("The start time must be within the last 24 hours or the next 7 days.");
    }
}

public sealed class StopMedicationOrderRequestValidator : AbstractValidator<StopMedicationOrderRequest>
{
    public StopMedicationOrderRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class AddAdHocDoseRequestValidator : AbstractValidator<AddAdHocDoseRequest>
{
    public AddAdHocDoseRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.DrugName).NotEmpty().When(x => x.MedicationOrderId is null).WithMessage("Enter the drug name.");
        RuleFor(x => x.DrugName).MaximumLength(200);
        RuleFor(x => x.Dose).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Route).MaximumLength(32);
        RuleFor(x => x.ScheduledAt)
            .Must(at => at is null || (at >= time.GetUtcNow().AddHours(-24) && at <= time.GetUtcNow().AddDays(1)))
            .WithMessage("The dose time must be within the last 24 hours or the next day.");
        RuleFor(x => x.Reason).SafeText(300);
        RuleFor(x => x.BatchNumber).MaximumLength(64);
        RuleFor(x => x.WitnessName).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class RecordAdministrationRequestValidator : AbstractValidator<RecordAdministrationRequest>
{
    public RecordAdministrationRequestValidator()
    {
        RuleFor(x => x.Outcome).IsInEnum().Must(o => o is MarStatus.Given or MarStatus.Held or MarStatus.Refused or MarStatus.Missed)
            .WithMessage("Record the dose as given, held, refused or missed.");
        RuleFor(x => x.Reason).NotEmpty().When(x => x.Outcome != MarStatus.Given).WithMessage("Give a reason when a dose is not given.");
        RuleFor(x => x.Reason).SafeText(300);
        RuleFor(x => x.BatchNumber).MaximumLength(64);
        RuleFor(x => x.WitnessName).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(500);
    }
}
