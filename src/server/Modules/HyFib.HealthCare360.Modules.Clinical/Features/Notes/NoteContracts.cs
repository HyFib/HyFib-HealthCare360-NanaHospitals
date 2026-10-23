using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Notes;

public sealed record WriteNoteRequest(Guid? EncounterId, Guid? AdmissionId, ClinicalNoteType Type, string? Title, string Body);

public sealed record EditNoteRequest(ClinicalNoteType Type, string? Title, string Body, uint Version);

public sealed record SignNoteRequest(uint Version);

public sealed class SignNoteRequestValidator : AbstractValidator<SignNoteRequest>
{
    public SignNoteRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed record AddendumRequest(string Body);

public sealed record ClinicalNoteDto(
    Guid Id,
    Guid PatientId,
    Guid? EncounterId,
    Guid? AdmissionId,
    ClinicalNoteType Type,
    string? Title,
    string Body,
    Guid? AuthorUserId,
    Guid? AuthorPractitionerId,
    string? AuthorName,
    Guid? AmendsNoteId,
    bool IsSigned,
    DateTimeOffset? SignedAt,
    DateTimeOffset CreatedAt,
    uint Version);

public sealed class WriteNoteRequestValidator : AbstractValidator<WriteNoteRequest>
{
    public WriteNoteRequestValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Body).NotEmpty().SafeText(20000);
    }
}

public sealed class EditNoteRequestValidator : AbstractValidator<EditNoteRequest>
{
    public EditNoteRequestValidator()
    {
        RuleFor(x => x.Title).MaximumLength(200);
        RuleFor(x => x.Body).NotEmpty().SafeText(20000);
    }
}

public sealed class AddendumRequestValidator : AbstractValidator<AddendumRequest>
{
    public AddendumRequestValidator() => RuleFor(x => x.Body).NotEmpty().SafeText(20000);
}
