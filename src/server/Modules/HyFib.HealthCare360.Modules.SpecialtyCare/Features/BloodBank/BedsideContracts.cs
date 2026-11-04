using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

/// <summary>A set of observations: temperature, pulse, blood pressure, respiratory rate, and oxygen saturation when measured.</summary>
public sealed record TransfusionObservationValues(
    decimal? TemperatureC, int? PulsePerMinute, int? SystolicMmHg, int? DiastolicMmHg, int? RespiratoryRate, int? SpO2Percent);

/// <summary>
/// The bedside check and the start: the patient's UHID and the unit number as read from the wristband and the bag, the
/// second member of staff's confirmation, and the observations before the start.
/// </summary>
public sealed record StartTransfusionRequest(
    string WristbandUhid, string BagUnitNumber, Guid WitnessConfirmationId, TransfusionObservationValues Baseline, string? ConsentDeferredReason);

/// <summary>Observations while the transfusion runs (at 15 minutes, or later).</summary>
public sealed record RecordTransfusionObservationRequest(TransfusionObservationKind Kind, DateTimeOffset? RecordedAt, TransfusionObservationValues Values, uint Version);

/// <summary>The end of a transfusion: completed or stopped, the volume given, and the observations at the end.</summary>
public sealed record EndTransfusionRequest(
    DateTimeOffset? EndedAt, bool Stopped, string? StopReason, int VolumeMl, TransfusionObservationValues Observation, uint Version);

public sealed record TransfusionObservationDto(
    TransfusionObservationKind Kind, DateTimeOffset RecordedAt, string RecordedByName, decimal? TemperatureC, int? PulsePerMinute, int? SystolicMmHg,
    int? DiastolicMmHg, int? RespiratoryRate, int? SpO2Percent);

public sealed record TransfusionDto(
    Guid Id, Guid IssueId, DateTimeOffset StartedAt, string StartedByName, string WitnessName, Guid? ConsentId, string? ConsentDeferredReason,
    TransfusionStatus Status, DateTimeOffset? EndedAt, string? EndedByName, int? VolumeMl, string? StopReason,
    IReadOnlyList<TransfusionObservationDto> Observations, uint Version);

/// <summary>
/// A unit issued for the patient, as the ward sees it: what it is, until when it may be started, the consent a start
/// would rely on (none recorded for this stay: null), and what became of it.
/// </summary>
public sealed record WardBloodUnit(
    Guid IssueId, string RequestNumber, string UnitNumber, BloodComponentType ComponentType, BloodGroup UnitBloodGroup, int VolumeMl,
    DateTimeOffset ExpiresAt, bool IsEmergencyRelease, DateTimeOffset? CrossMatchValidUntil, DateTimeOffset IssuedAt, string IssuedToName,
    DateTimeOffset StartBy, DateTimeOffset? ReturnedAt, DateTimeOffset? ConsentCapturedAt, TransfusionDto? Transfusion);

/// <summary>The patient's units on the ward, newest first.</summary>
public sealed record PatientBloodUnits(Guid PatientId, string Uhid, string PatientName, BloodGroup PatientBloodGroup, IReadOnlyList<WardBloodUnit> Units);

public sealed class TransfusionObservationValuesValidator : AbstractValidator<TransfusionObservationValues>
{
    public TransfusionObservationValuesValidator()
    {
        RuleFor(x => x.TemperatureC).NotNull().WithMessage("Enter the temperature.").InclusiveBetween(30m, 45m);
        RuleFor(x => x.PulsePerMinute).NotNull().WithMessage("Enter the pulse.").InclusiveBetween(20, 250);
        RuleFor(x => x.SystolicMmHg).NotNull().WithMessage("Enter the blood pressure.").InclusiveBetween(40, 300);
        RuleFor(x => x.DiastolicMmHg).NotNull().WithMessage("Enter the blood pressure.").InclusiveBetween(20, 200);
        RuleFor(x => x.DiastolicMmHg).LessThan(x => x.SystolicMmHg).When(x => x.SystolicMmHg is not null && x.DiastolicMmHg is not null)
            .WithMessage("The diastolic pressure is below the systolic.");
        RuleFor(x => x.RespiratoryRate).NotNull().WithMessage("Enter the respiratory rate.").InclusiveBetween(4, 80);
        RuleFor(x => x.SpO2Percent).InclusiveBetween(50, 100);
    }
}

public sealed class StartTransfusionRequestValidator : AbstractValidator<StartTransfusionRequest>
{
    public StartTransfusionRequestValidator()
    {
        RuleFor(x => x.WristbandUhid).NotEmpty().WithMessage("Scan or type the UHID on the patient's wristband.").MaximumLength(32);
        RuleFor(x => x.BagUnitNumber).NotEmpty().WithMessage("Scan or type the unit number on the bag.").MaximumLength(32);
        RuleFor(x => x.WitnessConfirmationId).NotEmpty().WithMessage("The second member of staff confirms the check at this screen.");
        RuleFor(x => x.Baseline).NotNull().SetValidator(new TransfusionObservationValuesValidator());
        RuleFor(x => x.ConsentDeferredReason).SafeText(500);
    }
}

public sealed class RecordTransfusionObservationRequestValidator : AbstractValidator<RecordTransfusionObservationRequest>
{
    public RecordTransfusionObservationRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Kind).Must(k => k is TransfusionObservationKind.FifteenMinutes or TransfusionObservationKind.Later)
            .WithMessage("Observations before the start and at the end are taken with the bedside check and with the end.");
        RuleFor(x => x.RecordedAt).Must(at => at is null || at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The time cannot be in the future.");
        RuleFor(x => x.Values).NotNull().SetValidator(new TransfusionObservationValuesValidator());
    }
}

public sealed class EndTransfusionRequestValidator : AbstractValidator<EndTransfusionRequest>
{
    public EndTransfusionRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.EndedAt).Must(at => at is null || at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The time cannot be in the future.");
        RuleFor(x => x.StopReason).NotEmpty().When(x => x.Stopped).WithMessage("Say why the transfusion was stopped.").SafeText(500);
        RuleFor(x => x.VolumeMl).InclusiveBetween(0, 1000);
        RuleFor(x => x.Observation).NotNull().SetValidator(new TransfusionObservationValuesValidator());
    }
}
