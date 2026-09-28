using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Credentials;

public sealed record CredentialDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    CredentialType Type,
    string Name,
    string Number,
    string? IssuingAuthority,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    CredentialExpiryStage Stage,
    int? DaysToExpiry,
    Guid? FileId,
    string? Notes,
    uint Version);

public sealed record SaveCredentialRequest(
    CredentialType Type,
    string Name,
    string Number,
    string? IssuingAuthority,
    DateOnly? IssuedOn,
    DateOnly? ExpiresOn,
    Guid? FileId,
    string? Notes,
    uint? Version);

public sealed class SaveCredentialRequestValidator : AbstractValidator<SaveCredentialRequest>
{
    public SaveCredentialRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().SafeText(150);
        RuleFor(x => x.Number).NotEmpty().SafeText(64);
        RuleFor(x => x.IssuingAuthority).SafeText(150);
        RuleFor(x => x.ExpiresOn)
            .Must((x, expires) => expires is null || x.IssuedOn is null || expires > x.IssuedOn)
            .WithMessage("The expiry date must be after the issue date.");
        RuleFor(x => x.ExpiresOn).NotNull().When(x => x.Type is CredentialType.BLS or CredentialType.ACLS)
            .WithMessage("BLS and ACLS certificates expire; enter the expiry date.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}
