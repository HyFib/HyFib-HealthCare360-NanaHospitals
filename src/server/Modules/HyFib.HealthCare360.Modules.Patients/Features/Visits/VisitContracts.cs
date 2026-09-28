using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Features.Visits;

public sealed record VisitListItem(
    Guid Id,
    string VisitNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    VisitType Type,
    VisitStatus Status,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? PractitionerId,
    string? PractitionerName,
    DateTimeOffset VisitedAt,
    DateTimeOffset? CompletedAt,
    bool IsFollowUp,
    bool IsFreeFollowUp,
    PayerType PayerType,
    bool IsMlc,
    string? TokenLabel,
    TokenStatus? TokenStatus);

public sealed record VisitDetail(
    Guid Id,
    string VisitNumber,
    Guid PatientId,
    VisitType Type,
    VisitStatus Status,
    Guid? DepartmentId,
    Guid? PractitionerId,
    Guid? AppointmentId,
    DateTimeOffset VisitedAt,
    DateTimeOffset? CompletedAt,
    bool IsFollowUp,
    bool IsFreeFollowUp,
    Guid? PreviousVisitId,
    PayerType PayerType,
    Guid? PayerId,
    ReferralSource ReferralSource,
    string? ReferredBy,
    string? ChiefComplaint,
    bool IsMlc,
    string? MlcNumber,
    string? Notes,
    string? CancelReason,
    QueueTokenDto? Token,
    uint Version);

public sealed record QueueTokenDto(
    Guid Id,
    Guid VisitId,
    Guid PatientId,
    Guid DepartmentId,
    Guid? PractitionerId,
    DateOnly TokenDate,
    int Sequence,
    string Label,
    TokenStatus Status,
    TokenPriority Priority,
    DateTimeOffset IssuedAt,
    DateTimeOffset? CalledAt,
    DateTimeOffset? ServiceStartedAt,
    DateTimeOffset? CompletedAt,
    string? Room,
    int CallCount);

public sealed record CreateVisitRequest(
    Guid PatientId,
    VisitType Type,
    Guid? DepartmentId,
    Guid? PractitionerId,
    Guid? AppointmentId,
    PayerType PayerType,
    Guid? PayerId,
    ReferralSource ReferralSource,
    string? ReferredBy,
    string? ChiefComplaint,
    bool IsMlc,
    string? MlcNumber,
    string? Notes,
    TokenPriority Priority,
    bool IssueToken = true,
    bool NotifyPatient = true);

public sealed record CancelVisitRequest(string Reason);

public sealed class CreateVisitRequestValidator : AbstractValidator<CreateVisitRequest>
{
    public CreateVisitRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.DepartmentId).NotNull()
            .When(x => x.Type is VisitType.OutPatient or VisitType.Emergency or VisitType.DayCare)
            .WithMessage("Choose a department.");
        RuleFor(x => x.DepartmentId).NotNull().When(x => x.IssueToken).WithMessage("A department is needed to issue a token.");
        RuleFor(x => x.ReferredBy).MaximumLength(200);
        RuleFor(x => x.ChiefComplaint).SafeText(500);
        RuleFor(x => x.MlcNumber).MaximumLength(64);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class CancelVisitRequestValidator : AbstractValidator<CancelVisitRequest>
{
    public CancelVisitRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
