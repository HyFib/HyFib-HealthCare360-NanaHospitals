using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Transfers;

/// <summary>
/// The sending ward asks for a move to a bed in another ward. <see cref="IsUrgent"/> is allowed only for a step-up or
/// step-down to or from critical care (ICU, NICU, PICU, HDU): the patient moves at once and the receiving ward records its
/// acceptance afterwards, within the hour.
/// </summary>
public sealed record RequestTransferRequest(
    Guid ToBedId,
    string Reason,
    string ClinicalSummary,
    Guid? ConsultantPractitionerId,
    Guid? DepartmentId,
    bool IsUrgent);

/// <summary><see cref="TransferredAt"/> may be back-dated as a direct transfer may (24 hours); it is ignored for an urgent move already made.</summary>
public sealed record AcceptTransferRequest(string? Note, DateTimeOffset? TransferredAt, uint Version);

public sealed record DecideTransferRequest(string Reason, uint Version);

public sealed record TransferRequestDto(
    Guid Id,
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Guid? FromWardId,
    string? FromWardName,
    string? FromBedLabel,
    Guid ToWardId,
    string ToWardName,
    Guid ToBedId,
    string ToBedLabel,
    string Reason,
    string ClinicalSummary,
    bool IsUrgent,
    TransferRequestStatus Status,
    DateTimeOffset RequestedAt,
    string? RequestedByName,
    DateTimeOffset? HoldUntil,
    DateTimeOffset? DecidedAt,
    string? DecidedByName,
    string? DecisionNote,
    DateTimeOffset? TransferredAt,
    DateTimeOffset? AcceptanceDueAt,
    bool IsAcceptanceOverdue,
    bool WasAcceptedLate,
    bool CanDecide,
    uint Version);

public sealed class RequestTransferRequestValidator : AbstractValidator<RequestTransferRequest>
{
    public RequestTransferRequestValidator()
    {
        RuleFor(x => x.ToBedId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
        RuleFor(x => x.ClinicalSummary).NotEmpty().SafeText(2000);
    }
}

public sealed class AcceptTransferRequestValidator : AbstractValidator<AcceptTransferRequest>
{
    public AcceptTransferRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Note).SafeText(500);
        RuleFor(x => x.TransferredAt)
            .Must(at => AdmissionRules.WithinWindow(at, time.GetUtcNow(), TimeSpan.FromHours(24)))
            .WithMessage("The transfer time must be within the last 24 hours and not in the future.");
    }
}

public sealed class DecideTransferRequestValidator : AbstractValidator<DecideTransferRequest>
{
    public DecideTransferRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(500);
}
