using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Services;

namespace HyFib.HealthCare360.Modules.Quality.Features.Capas;

public sealed record CapaListItem(
    Guid Id,
    string CapaNumber,
    CapaSource Source,
    Guid? IncidentId,
    string? IncidentNumber,
    Guid? RiskId,
    CapaType Type,
    string Action,
    Guid OwnerUserId,
    string? OwnerName,
    DateOnly DueDate,
    CapaStatus Status,
    bool IsOverdue,
    DateOnly? EffectivenessCheckDue);

public sealed record CapaDetail(
    Guid Id,
    string CapaNumber,
    CapaSource Source,
    Guid? IncidentId,
    Guid? RiskId,
    CapaType Type,
    string Action,
    Guid OwnerUserId,
    string? OwnerName,
    DateOnly DueDate,
    CapaStatus Status,
    bool IsOverdue,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? CompletionNote,
    DateOnly? EffectivenessCheckDue,
    bool? IsEffective,
    string? EffectivenessNote,
    DateTimeOffset? VerifiedAt,
    string? CancelReason,
    int ReworkCount,
    uint Version);

public sealed record CreateCapaRequest(
    CapaSource Source,
    Guid? IncidentId,
    Guid? RiskId,
    CapaType Type,
    string Action,
    Guid OwnerUserId,
    DateOnly DueDate);

public sealed record UpdateCapaRequest(string Action, Guid OwnerUserId, DateOnly DueDate, DateOnly? EffectivenessCheckDue, uint Version);

public sealed record CompleteCapaRequest(string Note);

public sealed record VerifyCapaRequest(bool Effective, string Note);

public sealed record CancelCapaRequest(string Reason);

public sealed class CreateCapaRequestValidator : AbstractValidator<CreateCapaRequest>
{
    public CreateCapaRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.Action).NotEmpty().SafeText(2000);
        RuleFor(x => x.OwnerUserId).NotEmpty();
        RuleFor(x => x.DueDate).Must(d => d >= time.LocalToday().AddDays(-1)).WithMessage("The due date cannot be in the past.");
        RuleFor(x => x.IncidentId).NotNull().When(x => x.Source == CapaSource.Incident).WithMessage("Choose the incident.");
        RuleFor(x => x.RiskId).NotNull().When(x => x.Source == CapaSource.Risk).WithMessage("Choose the risk.");
    }
}

public sealed class UpdateCapaRequestValidator : AbstractValidator<UpdateCapaRequest>
{
    public UpdateCapaRequestValidator()
    {
        RuleFor(x => x.Action).NotEmpty().SafeText(2000);
        RuleFor(x => x.OwnerUserId).NotEmpty();
    }
}

public sealed class CompleteCapaRequestValidator : AbstractValidator<CompleteCapaRequest>
{
    public CompleteCapaRequestValidator() => RuleFor(x => x.Note).NotEmpty().SafeText(2000);
}

public sealed class VerifyCapaRequestValidator : AbstractValidator<VerifyCapaRequest>
{
    public VerifyCapaRequestValidator() => RuleFor(x => x.Note).NotEmpty().SafeText(2000);
}

public sealed class CancelCapaRequestValidator : AbstractValidator<CancelCapaRequest>
{
    public CancelCapaRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(500);
}
