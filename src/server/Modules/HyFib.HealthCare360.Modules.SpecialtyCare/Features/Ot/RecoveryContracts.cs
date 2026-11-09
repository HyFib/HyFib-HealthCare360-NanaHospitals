using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

/// <summary>
/// The modified Aldrete score of the latest observations: each sign (points, or what is missing to score it), the total
/// once all five are scored, when the observations were taken, and why the score does not send the patient to the ward
/// now (<see cref="WardBlockedBy"/>, null when it does).
/// </summary>
public sealed record RecoveryScoreDto(
    IReadOnlyList<AldreteComponent> Components,
    int? Total,
    DateTimeOffset? At,
    IReadOnlyList<string> Missing,
    bool MeetsWardDischarge,
    string? WardBlockedBy);

/// <param name="HandoverRequired">The case had an anaesthetist, who hands over before the patient leaves recovery.</param>
public sealed record RecoveryRecordDto(
    Guid Id,
    Guid SurgeryBookingId,
    Guid PatientId,
    DateTimeOffset ReceivedAt,
    string ReceivedByName,
    AirwayDevice Airway,
    OxygenDelivery Oxygen,
    decimal? OxygenLitresPerMinute,
    int? PreAnaesthesiaSystolic,
    string? Notes,
    bool HandoverRequired,
    DateTimeOffset? HandoverAt,
    string? HandedOverByName,
    bool HandoverConfirmedAtScreen,
    string? HandoverNotes,
    IReadOnlyList<RecoveryObservation> Observations,
    RecoveryScoreDto Score,
    DateTimeOffset? DischargedAt,
    RecoveryDestination? Destination,
    int? DischargeScore,
    string? DischargedByName,
    string? OverrideReason,
    string? OverrideByName,
    uint Version);

/// <summary>
/// The signed-in nurse receives the patient in recovery, after the WHO Sign-out. <see cref="ReceivedAt"/> defaults to now
/// (it may be written a little after, not before the Sign-out). <see cref="PreAnaesthesiaSystolic"/>, the baseline the
/// circulation is scored against, defaults to the first systolic of the anaesthesia chart.
/// </summary>
public sealed record ReceiveInRecoveryRequest(
    AirwayDevice Airway,
    OxygenDelivery Oxygen,
    decimal? OxygenLitresPerMinute = null,
    int? PreAnaesthesiaSystolic = null,
    DateTimeOffset? ReceivedAt = null,
    string? Notes = null);

public sealed record SaveRecoveryRecordRequest(
    AirwayDevice Airway,
    OxygenDelivery Oxygen,
    decimal? OxygenLitresPerMinute,
    int? PreAnaesthesiaSystolic,
    string? Notes,
    uint Version);

/// <summary>
/// The anaesthetist's handover. Without <see cref="WitnessConfirmationId"/> the signed-in user is the anaesthetist (holds
/// <c>ot.anaesthesia.record</c>); with it, the anaesthetist confirmed at the nurse's screen (<c>POST /api/v1/auth/witness</c>,
/// purpose <c>ot.recovery_handover</c>, the patient).
/// </summary>
public sealed record RecoveryHandoverRequest(Guid? WitnessConfirmationId = null, string? Notes = null);

/// <summary>One row of the recovery chart; <see cref="At"/> defaults to now.</summary>
public sealed record AddRecoveryObservationRequest(
    int? HeartRate,
    int? Systolic,
    int? Diastolic,
    int? SpO2,
    bool OnOxygen,
    int? RespiratoryRate,
    decimal? TemperatureC,
    RecoveryConsciousness? Consciousness,
    RecoveryBreathing? Breathing,
    int? LimbsMoving,
    int? PainScore,
    Ponv? Ponv,
    DateTimeOffset? At = null);

public sealed record DischargeFromRecoveryRequest(RecoveryDestination Destination);

/// <summary>The anaesthetist sends the patient to the ward below the score, from their own sign-in, with the reason.</summary>
public sealed record OverrideRecoveryDischargeRequest(string Reason);

public sealed class ReceiveInRecoveryRequestValidator : AbstractValidator<ReceiveInRecoveryRequest>
{
    public ReceiveInRecoveryRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Airway).IsInEnum();
        RuleFor(x => x.Oxygen).IsInEnum();
        RuleFor(x => x.OxygenLitresPerMinute).InclusiveBetween(0.5m, 15m).When(x => x.OxygenLitresPerMinute is not null);
        RuleFor(x => x.PreAnaesthesiaSystolic).Systolic();
        RuleFor(x => x.ReceivedAt!.Value).NotInFuture(time).When(x => x.ReceivedAt is not null).WithName("receivedAt");
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class SaveRecoveryRecordRequestValidator : AbstractValidator<SaveRecoveryRecordRequest>
{
    public SaveRecoveryRecordRequestValidator()
    {
        RuleFor(x => x.Airway).IsInEnum();
        RuleFor(x => x.Oxygen).IsInEnum();
        RuleFor(x => x.OxygenLitresPerMinute).InclusiveBetween(0.5m, 15m).When(x => x.OxygenLitresPerMinute is not null);
        RuleFor(x => x.PreAnaesthesiaSystolic).Systolic();
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class RecoveryHandoverRequestValidator : AbstractValidator<RecoveryHandoverRequest>
{
    public RecoveryHandoverRequestValidator()
    {
        RuleFor(x => x.WitnessConfirmationId).NotEqual(Guid.Empty).When(x => x.WitnessConfirmationId is not null);
        RuleFor(x => x.Notes).SafeText(2000);
    }
}

public sealed class AddRecoveryObservationRequestValidator : AbstractValidator<AddRecoveryObservationRequest>
{
    public AddRecoveryObservationRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.At!.Value).NotInFuture(time).When(x => x.At is not null).WithName("at");
        RuleFor(x => x.HeartRate).Pulse();
        RuleFor(x => x.Systolic).Systolic();
        RuleFor(x => x.Diastolic).Diastolic();
        RuleFor(x => x.SpO2).Saturation();
        RuleFor(x => x.RespiratoryRate).InclusiveBetween(0, 80);
        RuleFor(x => x.TemperatureC).TemperatureC();
        RuleFor(x => x.Consciousness).IsInEnum().When(x => x.Consciousness is not null);
        RuleFor(x => x.Breathing).IsInEnum().When(x => x.Breathing is not null);
        RuleFor(x => x.Ponv).IsInEnum().When(x => x.Ponv is not null);
        RuleFor(x => x.LimbsMoving).InclusiveBetween(0, 4).WithMessage("Enter how many limbs move on command, 0 to 4.");
        RuleFor(x => x.PainScore).PainScore();
        RuleFor(x => x)
            .Must(x => x.HeartRate is not null || x.Systolic is not null || x.SpO2 is not null || x.RespiratoryRate is not null
                       || x.TemperatureC is not null || x.Consciousness is not null || x.Breathing is not null || x.LimbsMoving is not null
                       || x.PainScore is not null || x.Ponv is not null)
            .WithName("heartRate").WithMessage("Record at least one observation.");
    }
}

public sealed class DischargeFromRecoveryRequestValidator : AbstractValidator<DischargeFromRecoveryRequest>
{
    public DischargeFromRecoveryRequestValidator() => RuleFor(x => x.Destination).IsInEnum();
}

public sealed class OverrideRecoveryDischargeRequestValidator : AbstractValidator<OverrideRecoveryDischargeRequest>
{
    public OverrideRecoveryDischargeRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Give the reason the patient goes to the ward below the score.").SafeText(500);
}
