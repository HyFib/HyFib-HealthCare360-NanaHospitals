using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

public sealed record CreateCodingRequest(Guid PatientId, Guid? EncounterId, Guid? AdmissionId, Guid? VisitId, DateOnly? EpisodeDate, string? Notes);

public sealed record CodingLineRequest(CodingEntryKind Kind, string Code, bool IsPrimary);

public sealed record SaveCodingRequest(IReadOnlyList<CodingLineRequest> Entries, string? Notes, uint Version);

public sealed record VerifyCodingRequest(uint Version);

public sealed class VerifyCodingRequestValidator : AbstractValidator<VerifyCodingRequest>
{
    public VerifyCodingRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed record CodingEntryDto(Guid Id, CodingEntryKind Kind, string Code, string Title, bool IsPrimary, int Sequence);

public sealed record CodingDto(
    Guid Id,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? EncounterId,
    Guid? AdmissionId,
    Guid? VisitId,
    DateOnly? EpisodeDate,
    CodingStatus Status,
    Guid? CoderUserId,
    DateTimeOffset? CodedAt,
    Guid? VerifiedByUserId,
    DateTimeOffset? VerifiedAt,
    string? Notes,
    IReadOnlyList<CodingEntryDto> Entries,
    uint Version);

/// <summary>Diagnoses documented clinically for the episode, offered to the coder as a starting point.</summary>
public sealed record CodingSuggestion(string? Code, string Title, DiagnosisType Type, bool IsPrimary, string Source);

public sealed record MorbidityRow(
    string Key,
    string Title,
    string? Chapter,
    int Episodes,
    int Patients,
    int Male,
    int Female,
    int OtherOrUnknownGender,
    int Age0To14,
    int Age15To44,
    int Age45To64,
    int Age65Plus,
    int AgeUnknown);

/// <summary>Disease/morbidity index (MRD-06) by ICD code, block or chapter.</summary>
public sealed record MorbidityReport(DateOnly From, DateOnly To, string GroupBy, string Source, int TotalEpisodes, IReadOnlyList<MorbidityRow> Rows);

public sealed class CreateCodingRequestValidator : AbstractValidator<CreateCodingRequest>
{
    public CreateCodingRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x).Must(x => x.EncounterId is not null || x.AdmissionId is not null)
            .WithName("admissionId").WithMessage("Code either an admission or an encounter.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class SaveCodingRequestValidator : AbstractValidator<SaveCodingRequest>
{
    public SaveCodingRequestValidator()
    {
        RuleFor(x => x.Entries).NotEmpty().Must(e => e is null || e.Count <= 40).WithMessage("At most 40 codes per episode.");
        RuleForEach(x => x.Entries).ChildRules(line => line.RuleFor(l => l.Code).NotEmpty().MaximumLength(16));
        RuleFor(x => x.Notes).SafeText(1000);
    }
}
