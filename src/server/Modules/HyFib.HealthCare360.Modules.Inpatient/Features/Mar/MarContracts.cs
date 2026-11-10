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
    IReadOnlyList<TimeOnly> RoundTimes,
    bool IsHighAlert = false,
    MedicationOrderMode Mode = MedicationOrderMode.Written,
    bool IsEmergency = false,
    Guid? OrderedByPractitionerId = null,
    string? OrderedByName = null,
    string? EnteredByName = null,
    bool AwaitingCountersignature = false,
    DateTimeOffset? CountersignDueAt = null,
    bool CountersignOverdue = false,
    DateTimeOffset? CountersignedAt = null,
    string? CountersignedByName = null,
    bool StopPending = false,
    DateTimeOffset? StopRequestedAt = null,
    string? StopRequestedByName = null,
    string? StopRequestReason = null,
    bool StopCountersignOverdue = false,
    bool DosesHeld = false,
    bool CanCountersign = false,
    uint Version = 0);

/// <summary>
/// A medicine put on the ward's chart. A doctor writing in their own name (<paramref name="OrderedByPractitionerId"/> their
/// own practitioner record, or none) signs it at once. Anyone else writes the doctor's verbal or telephone order: the
/// doctor named, <paramref name="Mode"/> (verbal or telephone) and <paramref name="ReadBack"/> (read back to the doctor, who
/// confirmed it) are required, and the doctor countersigns it. <paramref name="Emergency"/>: a verbal order of a
/// high-alert or controlled medicine to be given before the doctor countersigns it. A medicine picked from the drug master
/// (<paramref name="DrugItemId"/>) takes its flags from it; one typed in is handled as high-alert.
/// </summary>
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
    Guid? OrderedByPractitionerId,
    bool IsHighAlert = false,
    MedicationOrderMode? Mode = null,
    bool ReadBack = false,
    bool Emergency = false);

/// <summary>A prescriber stops the order at once; anyone else asks for the stop, which the doctor countersigns.</summary>
public sealed record StopMedicationOrderRequest(string Reason);

/// <summary>The doctor named countersigns a verbal or telephone order, as it was when they saw it.</summary>
public sealed record CountersignMedicationOrderRequest(uint Version);

/// <summary>The doctor confirms (countersigns) the stop a nurse asked for, as it was when they saw it.</summary>
public sealed record ConfirmStopRequest(uint Version);

/// <summary>The doctor declines the stop a nurse asked for: the order goes on.</summary>
public sealed record DeclineStopRequest(string Reason, uint Version);

/// <summary>A verbal or telephone order, or a nurse's stop, waiting for the doctor's countersignature, with its patient and bed.</summary>
public sealed record AwaitingCountersignatureItem(
    MedicationOrderDto Order,
    Guid AdmissionId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Guid? WardId,
    string? WardName,
    string? BedLabel);

/// <summary>A medicine of the drug master for the ward's order form: its flags decide whether its doses are witnessed.</summary>
public sealed record WardDrugDto(Guid Id, string Code, string Name, string? GenericName, string? Strength, string? DosageForm, bool IsControlled, bool IsHighAlert);

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
    uint Version,
    bool NeedsWitness = false,
    string? HeldBecause = null);

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
    Guid? WitnessConfirmationId,
    string? Notes);

/// <summary>
/// A dose as given, held, refused or missed. <paramref name="WitnessConfirmationId"/>: for a dose that is witnessed, a second
/// nurse's confirmation, given at this screen with their own user name and password (<c>POST /api/v1/auth/witness</c>,
/// purpose <c>mar.dose</c>).
/// </summary>
public sealed record RecordAdministrationRequest(
    MarStatus Outcome,
    DateTimeOffset? AdministeredAt,
    string? Reason,
    string? BatchNumber,
    Guid? WitnessConfirmationId,
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
    uint Version,
    bool NeedsWitness = false,
    string? HeldBecause = null);

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
        RuleFor(x => x.DrugItemId).OptionalId();
        RuleFor(x => x.OrderedByPractitionerId).OptionalId();
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Emergency).Equal(false).When(x => x.Mode is not (MedicationOrderMode.Verbal or MedicationOrderMode.Telephone))
            .WithMessage("Only a verbal or telephone order is marked as an emergency.");
    }
}

public sealed class StopMedicationOrderRequestValidator : AbstractValidator<StopMedicationOrderRequest>
{
    public StopMedicationOrderRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class CountersignMedicationOrderRequestValidator : AbstractValidator<CountersignMedicationOrderRequest>
{
    public CountersignMedicationOrderRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed class ConfirmStopRequestValidator : AbstractValidator<ConfirmStopRequest>
{
    public ConfirmStopRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed class DeclineStopRequestValidator : AbstractValidator<DeclineStopRequest>
{
    public DeclineStopRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
        RuleFor(x => x.Version).SeenVersion();
    }
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
        RuleFor(x => x.WitnessConfirmationId).OptionalId();
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
        RuleFor(x => x.WitnessConfirmationId).OptionalId();
        RuleFor(x => x.Notes).SafeText(500);
    }
}
