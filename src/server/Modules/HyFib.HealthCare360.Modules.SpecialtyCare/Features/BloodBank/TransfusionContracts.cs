using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;

public sealed record BloodUnitDto(
    Guid Id, string UnitNumber, Guid DonationId, BloodComponentType ComponentType, BloodGroup BloodGroup, int VolumeMl, DateTimeOffset CollectedAt,
    DateTimeOffset ExpiresAt, BloodUnitStatus Status, TtiPanel Tti, string? StorageLocation, Guid? ReservedForRequestId, DateTimeOffset? IssuedAt,
    string? DiscardReason, bool IsExpired);

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
    DateTimeOffset? RequiredBy);

public sealed record CrossMatchDto(
    Guid Id, Guid ComponentId, string UnitNumber, CrossmatchMethod Method, CrossmatchResult Result, DateTimeOffset PerformedAt, string? PerformedByName,
    string? Remarks, bool IsValid, ChargeStatus ChargeStatus);

public sealed record BloodIssueDto(
    Guid Id, Guid RequestId, Guid ComponentId, string UnitNumber, BloodComponentType ComponentType, BloodGroup UnitBloodGroup, bool IsEmergencyRelease,
    DateTimeOffset IssuedAt, string? IssuedByName, string IssuedToName, DateTimeOffset? ReturnedAt, bool? Restocked, string? ReturnNotes,
    ChargeStatus ChargeStatus, string? ChargeError);

public sealed record BloodRequestDetail(
    Guid Id, string RequestNumber, Guid PatientId, string Uhid, string PatientName, Guid? AdmissionId, Guid? VisitId, Guid? OrderItemId,
    BloodComponentType ComponentType, int UnitsRequested, int UnitsReserved, int UnitsIssued, BloodRequestUrgency Urgency, string Indication,
    DateTimeOffset? RequiredBy, string? RequestedByName, decimal? HaemoglobinGdl, int? PlateletCount, BloodGroup PatientBloodGroup,
    DateTimeOffset? GroupedAt, string? GroupedByName, string? EmergencyReleaseAuthorisedBy, string? EmergencyReleaseReason, BloodRequestStatus Status,
    string? CancelReason, IReadOnlyList<CrossMatchDto> CrossMatches, IReadOnlyList<BloodIssueDto> Issues, uint Version);

/// <summary>A unit the blood bank can offer for a request (ABO/Rh compatible, in date, first-expiry-first-out order).</summary>
public sealed record CompatibleUnitDto(Guid Id, string UnitNumber, BloodGroup BloodGroup, int VolumeMl, DateTimeOffset ExpiresAt, bool ReservedForThisRequest);

public sealed record GroupPatientRequest(BloodGroup BloodGroup);

public sealed record EmergencyReleaseRequest(string AuthorisedBy, string Reason);

public sealed record BloodReasonRequest(string Reason);

public sealed record RecordCrossMatchRequest(Guid ComponentId, CrossmatchMethod Method, CrossmatchResult Result, string? Remarks);

public sealed record IssueBloodRequest(Guid ComponentId, string IssuedToName);

public sealed record ReturnBloodRequest(bool TemperatureMaintained, string? Notes);

public sealed record ReportReactionRequest(
    DateTimeOffset OnsetAt, IReadOnlyList<ReactionSymptom> Symptoms, string? Vitals, bool TransfusionStopped, int? VolumeTransfusedMl, string? ActionsTaken,
    ReactionSeverity Severity);

public sealed record CloseReactionRequest(ReactionType Type, ReactionSeverity Severity, string Findings, bool ReportedToHaemovigilance);

public sealed record TransfusionReactionDto(
    Guid Id, Guid IssueId, Guid RequestId, Guid PatientId, string Uhid, string PatientName, string UnitNumber, DateTimeOffset ReportedAt,
    string? ReportedByName, DateTimeOffset OnsetAt, IReadOnlyList<ReactionSymptom> Symptoms, string? Vitals, bool TransfusionStopped,
    int? VolumeTransfusedMl, string? ActionsTaken, ReactionSeverity Severity, ReactionType Type, ReactionStatus Status, string? InvestigationFindings,
    bool ReportedToHaemovigilance, DateTimeOffset? ClosedAt, string? ClosedByName);

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
    public GroupPatientRequestValidator() =>
        RuleFor(x => x.BloodGroup).NotEqual(BloodGroup.Unknown).WithMessage("Enter the patient's ABO and Rh(D) group.");
}

public sealed class EmergencyReleaseRequestValidator : AbstractValidator<EmergencyReleaseRequest>
{
    public EmergencyReleaseRequestValidator()
    {
        RuleFor(x => x.AuthorisedBy).NotEmpty().MaximumLength(200).WithMessage("Name the doctor authorising uncross-matched release.");
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(10).SafeText(500);
    }
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
        RuleFor(x => x.Findings).NotEmpty().SafeText(4000);
    }
}
