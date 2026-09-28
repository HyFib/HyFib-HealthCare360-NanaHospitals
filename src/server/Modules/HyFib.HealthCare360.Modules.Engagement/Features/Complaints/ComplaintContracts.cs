using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Complaints;

public sealed record ComplaintListItem(
    Guid Id,
    string ComplaintNumber,
    ComplaintSource Source,
    ComplaintStatus Status,
    ComplaintPriority Priority,
    string? Category,
    Guid? DepartmentId,
    Guid? PatientId,
    Guid? OwnerUserId,
    DateTimeOffset RaisedAt,
    DateTimeOffset DueAt,
    bool IsOverdue,
    double? TurnaroundHours);

public sealed record ComplaintDetail(
    Guid Id,
    string ComplaintNumber,
    ComplaintSource Source,
    ComplaintStatus Status,
    ComplaintPriority Priority,
    string? Category,
    string Description,
    Guid? DepartmentId,
    Guid? PatientId,
    Guid? FeedbackResponseId,
    string? ContactName,
    string? ContactPhone,
    Guid? OwnerUserId,
    string? Resolution,
    DateTimeOffset RaisedAt,
    DateTimeOffset DueAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClosedAt,
    int ReopenCount,
    bool IsOverdue,
    uint Version);

/// <summary>A complaint recorded by staff (walk-in, phone, e-mail).</summary>
public sealed record CreateComplaintRequest(
    ComplaintSource Source,
    Guid? PatientId,
    Guid? DepartmentId,
    string? Category,
    string Description,
    string? ContactName,
    string? ContactPhone,
    ComplaintPriority Priority,
    Guid? OwnerUserId);

public sealed record UpdateComplaintRequest(Guid? OwnerUserId, ComplaintPriority Priority, Guid? DepartmentId, string? Category, uint Version);

public sealed record ChangeComplaintStatusRequest(ComplaintStatus Status, string? Note);

public sealed class CreateComplaintRequestValidator : AbstractValidator<CreateComplaintRequest>
{
    public CreateComplaintRequestValidator()
    {
        RuleFor(x => x.Source).NotEqual(ComplaintSource.Feedback).WithMessage("Complaints from feedback are raised automatically.");
        RuleFor(x => x.Description).NotEmpty().SafeText(4000);
        RuleFor(x => x.Category).SafeText(64);
        RuleFor(x => x.ContactName).SafeText(200);
        RuleFor(x => x.ContactPhone).PhoneNumber();
    }
}

public sealed class UpdateComplaintRequestValidator : AbstractValidator<UpdateComplaintRequest>
{
    public UpdateComplaintRequestValidator() => RuleFor(x => x.Category).SafeText(64);
}

public sealed class ChangeComplaintStatusRequestValidator : AbstractValidator<ChangeComplaintStatusRequest>
{
    public ChangeComplaintStatusRequestValidator() => RuleFor(x => x.Note).SafeText(2000);
}
