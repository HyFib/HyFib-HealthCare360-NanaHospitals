using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;

/// <summary>Either an ICD code from the local catalogue (title filled from it) or a free-text working diagnosis.</summary>
public sealed record AddDiagnosisRequest(string? IcdCode, string? Title, DiagnosisType Type, bool IsPrimary, DateOnly? OnsetDate, DiagnosisStatus Status, string? Notes);

public sealed record UpdateDiagnosisRequest(DiagnosisType Type, bool IsPrimary, DiagnosisStatus Status, DateOnly? OnsetDate, string? Notes, uint Version);

public sealed record DiagnosisDto(
    Guid Id,
    Guid PatientId,
    Guid? EncounterId,
    string? IcdCode,
    string Title,
    DiagnosisType Type,
    bool IsPrimary,
    DateOnly? OnsetDate,
    DiagnosisStatus Status,
    DateOnly? ResolvedOn,
    DateTimeOffset RecordedAt,
    string? Notes,
    uint Version);

public sealed class AddDiagnosisRequestValidator : AbstractValidator<AddDiagnosisRequest>
{
    public AddDiagnosisRequestValidator()
    {
        RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.IcdCode) || !string.IsNullOrWhiteSpace(x.Title))
            .WithName("icdCode").WithMessage("Choose an ICD code or type the diagnosis.");
        RuleFor(x => x.IcdCode).MaximumLength(16);
        RuleFor(x => x.Title).MaximumLength(300);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class UpdateDiagnosisRequestValidator : AbstractValidator<UpdateDiagnosisRequest>
{
    public UpdateDiagnosisRequestValidator() => RuleFor(x => x.Notes).SafeText(1000);
}
