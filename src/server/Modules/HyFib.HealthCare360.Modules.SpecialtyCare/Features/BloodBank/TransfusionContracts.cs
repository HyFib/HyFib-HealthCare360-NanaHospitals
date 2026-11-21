using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

/// <param name="AwaitingVerification">In quarantine with results that clear it, until a second officer verifies them.</param>
public sealed record BloodUnitDto(
    Guid Id, string UnitNumber, Guid DonationId, BloodComponentType ComponentType, BloodGroup BloodGroup, int VolumeMl, DateTimeOffset CollectedAt,
    DateTimeOffset ExpiresAt, BloodUnitStatus Status, TtiPanel Tti, string? StorageLocation, Guid? ReservedForRequestId, DateTimeOffset? IssuedAt,
    string? DiscardReason, bool IsExpired, bool AwaitingVerification);

public sealed record DiscardUnitRequest(string Reason);

public sealed record InventoryCell(BloodComponentType Component, BloodGroup BloodGroup, int Available, int Reserved, int Quarantine, int ExpiringSoon);

public sealed record ExpiringUnit(Guid Id, string UnitNumber, BloodComponentType Component, BloodGroup BloodGroup, DateTimeOffset ExpiresAt, BloodUnitStatus Status);

/// <summary>Stock by group and component with units expiring within 3 days (BB-05).</summary>
public sealed record BloodInventoryDashboard(
    DateTimeOffset AsOf, IReadOnlyList<InventoryCell> Stock, IReadOnlyList<ExpiringUnit> ExpiringSoon, int OpenRequests, int EmergencyRequests,
    int UnitsInQuarantine);

public sealed record CreateBloodRequestRequest(
    Guid PatientId, Guid? AdmissionId, Guid? VisitId, BloodComponentType ComponentType, int Units, BloodRequestUrgency Urgency, string Indication,
    DateTimeOffset? RequiredBy, decimal? HaemoglobinGdl, int? PlateletCount);

public sealed record BloodRequestListItem(
    Guid Id, string RequestNumber, Guid PatientId, string Uhid, string PatientName, BloodComponentType ComponentType, int UnitsRequested,
    int UnitsReserved, int UnitsIssued, BloodRequestUrgency Urgency, BloodRequestStatus Status, BloodGroup PatientBloodGroup, DateTimeOffset CreatedAt,
    DateTimeOffset? RequiredBy)
{
    /// <summary>The patient's first grouping waits for a second, separately drawn sample to confirm it (the two-sample rule).</summary>
    public bool AwaitingSecondSample { get; init; }
}

public sealed record CrossMatchDto(
    Guid Id, Guid ComponentId, string UnitNumber, CrossmatchMethod Method, CrossmatchResult Result, DateTimeOffset PerformedAt,
    Guid? PerformedByUserId, string? PerformedByName, string? Remarks, bool IsValid, ChargeStatus ChargeStatus);

/// <param name="IssuedOnOwnChecks">Issued on checks the issuer alone made (no second officer at the facility), for the medical officer's review.</param>
public sealed record BloodIssueDto(
    Guid Id, Guid RequestId, Guid ComponentId, string UnitNumber, BloodComponentType ComponentType, BloodGroup UnitBloodGroup, bool IsEmergencyRelease,
    DateTimeOffset IssuedAt, string? IssuedByName, string IssuedToName, DateTimeOffset? ReturnedAt, bool? Restocked, string? ReturnNotes,
    ChargeStatus ChargeStatus, string? ChargeError, bool IssuedOnOwnChecks);

/// <param name="EarlierGroup">The group of the patient's earlier requests, when this request's sample grouped otherwise.</param>
/// <param name="GroupingHeld">
/// Nothing group-specific is cross-matched or issued on the grouping until another officer groups a new sample: it disagreed
/// with the patient's earlier requests, it waits for a second sample (<see cref="AwaitingSecondSample"/>) and no doctor has
/// authorised going ahead on the one, or a sample disagreed with the one it rests on, a later sample of its own or one
/// grouped on another request of the patient (<see cref="DisagreeingSampleGroup"/>).
/// </param>
/// <param name="SeparateVerifier">The facility asks for a second officer (<c>bloodbank.separateVerifier</c>).</param>
public sealed record BloodRequestDetail(
    Guid Id, string RequestNumber, Guid PatientId, string Uhid, string PatientName, Guid? AdmissionId, Guid? VisitId, Guid? OrderItemId,
    BloodComponentType ComponentType, int UnitsRequested, int UnitsReserved, int UnitsIssued, BloodRequestUrgency Urgency, string Indication,
    DateTimeOffset? RequiredBy, string? RequestedByName, decimal? HaemoglobinGdl, int? PlateletCount, BloodGroup PatientBloodGroup,
    DateTimeOffset? GroupedAt, Guid? GroupedByUserId, string? GroupedByName, BloodGroup? EarlierGroup, BloodGroup? FirstSampleGroup,
    DateTimeOffset? RegroupedAt, bool GroupingHeld, string? EmergencyReleaseAuthorisedBy, Guid? EmergencyReleaseByUserId,
    DateTimeOffset? EmergencyReleaseAt, string? EmergencyReleaseReason, bool SeparateVerifier, BloodRequestStatus Status, string? CancelReason,
    IReadOnlyList<CrossMatchDto> CrossMatches, IReadOnlyList<BloodIssueDto> Issues, uint Version)
{
    /// <summary>When the sample the grouping uses was drawn and who drew it, and when the blood bank received it.</summary>
    public DateTimeOffset? SampleDrawnAt { get; init; }

    public string? SampleDrawnBy { get; init; }
    public DateTimeOffset? SampleReceivedAt { get; init; }

    /// <summary>When the first sample was drawn and who drew it, once a new sample's grouping replaced or confirmed it.</summary>
    public DateTimeOffset? FirstSampleDrawnAt { get; init; }

    public string? FirstSampleDrawnBy { get; init; }

    /// <summary>
    /// The patient had no group on record when the first sample was grouped (the two-sample rule): a second, separately drawn
    /// sample confirms it, grouped by another officer as a new sample.
    /// </summary>
    public bool SecondSampleRequired { get; init; }

    /// <summary>The second sample is not grouped yet.</summary>
    public bool AwaitingSecondSample { get; init; }

    /// <summary>
    /// The group of a sample that disagreed with the one the request rests on while it waited for its second sample (possible
    /// wrong blood in tube): a later sample of its own, or one grouped on another request of the patient
    /// (<see cref="DisagreeingSampleRequestNumber"/>), recorded or found there now. With when it was grouped (when recorded),
    /// when it was drawn and who drew it. The request is held, whatever a doctor authorised on one sample, until a sample
    /// drawn separately agrees with the first.
    /// </summary>
    public BloodGroup? DisagreeingSampleGroup { get; init; }

    public DateTimeOffset? DisagreeingSampleAt { get; init; }
    public DateTimeOffset? DisagreeingSampleDrawnAt { get; init; }
    public string? DisagreeingSampleDrawnBy { get; init; }

    /// <summary>The patient's other request the disagreeing sample was grouped on; none for a sample of this request's own.</summary>
    public string? DisagreeingSampleRequestNumber { get; init; }

    /// <summary>The doctor who authorised group-specific blood on one sample's grouping from their own sign-in, when, and why.</summary>
    public string? SingleSampleOverrideBy { get; init; }

    public Guid? SingleSampleOverrideByUserId { get; init; }
    public DateTimeOffset? SingleSampleOverrideAt { get; init; }
    public string? SingleSampleOverrideReason { get; init; }
}

/// <summary>A unit the blood bank can offer for a request (ABO/Rh compatible, in date, first-expiry-first-out order).</summary>
public sealed record CompatibleUnitDto(Guid Id, string UnitNumber, BloodGroup BloodGroup, int VolumeMl, DateTimeOffset ExpiresAt, bool ReservedForThisRequest);

/// <param name="SampleDrawnAt">When the sample was drawn from the patient, as the tube and the form say: not when it reached the blood bank.</param>
/// <param name="SampleDrawnBy">Who drew it: the collector named on the tube and the form.</param>
/// <param name="NewSample">
/// A new sample, drawn separately from the first and grouped by another officer, for a request held because its grouping
/// disagreed with the patient's earlier ones, or the second sample that confirms the group of a patient with none on record.
/// </param>
public sealed record GroupPatientRequest(BloodGroup BloodGroup, DateTimeOffset? SampleDrawnAt, string? SampleDrawnBy, bool NewSample = false);

/// <summary>The treating doctor authorises uncross-matched release from their own sign-in, with the clinical reason.</summary>
public sealed record EmergencyReleaseRequest(string Reason);

/// <summary>
/// The treating doctor authorises group-specific blood on one sample's grouping, before a second sample confirms it, from
/// their own sign-in, with the clinical reason (at least 10 characters): the emergency override, for emergency requests only.
/// </summary>
public sealed record SingleSampleOverrideRequest(string Reason);

public sealed record BloodReasonRequest(string Reason);

public sealed record RecordCrossMatchRequest(Guid ComponentId, CrossmatchMethod Method, CrossmatchResult Result, string? Remarks);

public sealed record IssueBloodRequest(Guid ComponentId, string IssuedToName);

public sealed record ReturnBloodRequest(bool TemperatureMaintained, string? Notes);

public sealed record ReportReactionRequest(
    DateTimeOffset OnsetAt, IReadOnlyList<ReactionSymptom> Symptoms, string? Vitals, bool TransfusionStopped, int? VolumeTransfusedMl, string? ActionsTaken,
    ReactionSeverity Severity);

public sealed record CloseReactionRequest(
    ReactionType Type, ReactionSeverity Severity, string Findings, bool ReportedToHaemovigilance, ReactionImputability Imputability = ReactionImputability.NotAssessed);

/// <summary>The blood bank's classification while the reaction is investigated; lowering severity or imputability needs a reason and waits.</summary>
public sealed record ClassifyReactionRequest(ReactionType Type, ReactionSeverity Severity, ReactionImputability Imputability, string? Reason, uint Version);

public sealed record TransfusionReactionDto(
    Guid Id, Guid IssueId, Guid RequestId, Guid PatientId, string Uhid, string PatientName, string UnitNumber, DateTimeOffset ReportedAt,
    string? ReportedByName, DateTimeOffset OnsetAt, IReadOnlyList<ReactionSymptom> Symptoms, string? Vitals, bool TransfusionStopped,
    int? VolumeTransfusedMl, string? ActionsTaken, ReactionSeverity Severity, ReactionType Type, ReactionStatus Status, string? InvestigationFindings,
    bool ReportedToHaemovigilance, DateTimeOffset? ClosedAt, string? ClosedByName)
{
    public ReactionImputability Imputability { get; init; }

    /// <summary>Who lowered the classification, who asked, and why, when it was lowered.</summary>
    public string? LoweredByName { get; init; }

    public string? LowerRequestedByName { get; init; }
    public string? LowerReason { get; init; }

    /// <summary>A lowering asked for and waiting for the medical officer, if any.</summary>
    public Guid? LowerPendingApprovalId { get; init; }

    public uint Version { get; init; }
}

public sealed class DiscardUnitRequestValidator : AbstractValidator<DiscardUnitRequest>
{
    public DiscardUnitRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(500);
}

public sealed class CreateBloodRequestRequestValidator : AbstractValidator<CreateBloodRequestRequest>
{
    public CreateBloodRequestRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Units).InclusiveBetween(1, 20);
        RuleFor(x => x.Indication).NotEmpty().WithMessage("State the clinical indication for transfusion.").SafeText(1000);
        RuleFor(x => x.HaemoglobinGdl).InclusiveBetween(1m, 25m);
        RuleFor(x => x.PlateletCount).InclusiveBetween(0, 2_000_000);
    }
}

public sealed class GroupPatientRequestValidator : AbstractValidator<GroupPatientRequest>
{
    /// <summary>How long ago a sample grouped for transfusion may have been drawn: older ones are too old to cross-match on.</summary>
    public static readonly TimeSpan OldestSample = TimeSpan.FromDays(7);

    public GroupPatientRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.BloodGroup).NotEqual(BloodGroup.Unknown).WithMessage("Enter the patient's ABO and Rh(D) group.");
        RuleFor(x => x.SampleDrawnAt).NotNull().WithMessage("Enter when the sample was drawn, as the tube or the form says.")
            .Must(at => at is null || at <= time.GetUtcNow().AddMinutes(5)).WithMessage("The time cannot be in the future.")
            .Must(at => at is null || at >= time.GetUtcNow() - OldestSample)
            .WithMessage("A sample drawn more than 7 days ago is too old to group for transfusion: ask for a fresh one.");
        RuleFor(x => x.SampleDrawnBy).NotEmpty().WithMessage("Enter who drew the sample, as the tube or the form names them.").SafeText(200);
    }
}

public sealed class EmergencyReleaseRequestValidator : AbstractValidator<EmergencyReleaseRequest>
{
    public EmergencyReleaseRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).SafeText(500);
}

public sealed class SingleSampleOverrideRequestValidator : AbstractValidator<SingleSampleOverrideRequest>
{
    public SingleSampleOverrideRequestValidator() =>
        RuleFor(x => x.Reason).Must(r => r is not null && r.Trim().Length >= BloodRequest.MinimumOverrideReason)
            .WithMessage("Give the clinical reason for issuing blood on one sample (at least 10 characters).").SafeText(500);
}

public sealed class BloodReasonRequestValidator : AbstractValidator<BloodReasonRequest>
{
    public BloodReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(500);
}

public sealed class RecordCrossMatchRequestValidator : AbstractValidator<RecordCrossMatchRequest>
{
    public RecordCrossMatchRequestValidator()
    {
        RuleFor(x => x.ComponentId).NotEmpty();
        RuleFor(x => x.Remarks).SafeText(500);
    }
}

public sealed class IssueBloodRequestValidator : AbstractValidator<IssueBloodRequest>
{
    public IssueBloodRequestValidator()
    {
        RuleFor(x => x.ComponentId).NotEmpty();
        RuleFor(x => x.IssuedToName).NotEmpty().WithMessage("Enter the name of the staff member collecting the unit.").MaximumLength(200);
    }
}

public sealed class ReturnBloodRequestValidator : AbstractValidator<ReturnBloodRequest>
{
    public ReturnBloodRequestValidator() => RuleFor(x => x.Notes).SafeText(500);
}

public sealed class ReportReactionRequestValidator : AbstractValidator<ReportReactionRequest>
{
    public ReportReactionRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.OnsetAt).NotInFuture(time);
        RuleFor(x => x.Symptoms).NotEmpty().WithMessage("Select the signs and symptoms observed.");
        RuleFor(x => x.Vitals).SafeText(500);
        RuleFor(x => x.VolumeTransfusedMl).InclusiveBetween(0, 1000);
        RuleFor(x => x.ActionsTaken).SafeText(2000);
    }
}

public sealed class CloseReactionRequestValidator : AbstractValidator<CloseReactionRequest>
{
    public CloseReactionRequestValidator()
    {
        RuleFor(x => x.Type).NotEqual(ReactionType.Unclassified).WithMessage("Classify the reaction.");
        RuleFor(x => x.Imputability).IsInEnum().NotEqual(ReactionImputability.NotAssessed).WithMessage("Assess the imputability.");
        RuleFor(x => x.Findings).NotEmpty().SafeText(4000);
    }
}

public sealed class ClassifyReactionRequestValidator : AbstractValidator<ClassifyReactionRequest>
{
    public ClassifyReactionRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Severity).IsInEnum();
        RuleFor(x => x.Imputability).IsInEnum();
        RuleFor(x => x.Reason).SafeText(1000);
        RuleFor(x => x.Version).SeenVersion();
    }
}
