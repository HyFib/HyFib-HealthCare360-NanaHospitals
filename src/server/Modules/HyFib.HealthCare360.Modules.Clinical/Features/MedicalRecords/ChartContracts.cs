using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;

/// <summary>A chart of an admission or encounter, or (with <see cref="Paper"/>) of a file that exists only on paper.</summary>
public sealed record CreateMedicalRecordRequest(
    Guid PatientId,
    Guid? EncounterId,
    Guid? AdmissionId,
    Guid? VisitId,
    Guid? AssignedPractitionerId,
    DateOnly? DueDate,
    bool IsMlc,
    PaperChartDetails? Paper = null);

/// <summary>
/// A paper file: the number written on it, whether it is of an in-patient stay, the episode's day (and discharge day of
/// a stay), and what it holds. The retention period counts from the episode, not from the day the chart was entered.
/// </summary>
public sealed record PaperChartDetails(string FileNumber, bool IsInpatient, DateOnly EpisodeFrom, DateOnly? EpisodeTo, string? Note);

public sealed record PaperChartDto(string FileNumber, bool IsInpatient, DateOnly? EpisodeFrom, DateOnly? EpisodeTo, string? Note);

public sealed record AddDeficiencyRequest(DeficiencyType Type, string Description, Guid? AssignedPractitionerId, DateOnly? DueDate);

public sealed record ResolveDeficiencyRequest(string? Note);

public sealed record ArchiveMedicalRecordRequest(string Location);

public sealed record ChartDeficiencyDto(
    Guid Id,
    DeficiencyType Type,
    string Description,
    string? Reference,
    Guid? AssignedPractitionerId,
    string? AssignedPractitionerName,
    DateOnly? DueDate,
    DateTimeOffset RaisedAt,
    bool IsSystemDetected,
    DateTimeOffset? ResolvedAt,
    string? ResolutionNote);

public sealed record MedicalRecordDto(
    Guid Id,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? EncounterId,
    Guid? AdmissionId,
    Guid? VisitId,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? DischargedAt,
    MedicalRecordStatus Status,
    Guid? AssignedPractitionerId,
    DateOnly? DueDate,
    bool IsOverdue,
    bool IsMlc,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? ArchivedAt,
    string? ArchiveLocation,
    DateOnly? RetainUntil,
    int OpenDeficiencies,
    IReadOnlyList<ChartDeficiencyDto> Deficiencies,
    uint Version,
    PaperChartDto? Paper = null);

public sealed record CreateRecordRequestRequest(
    Guid PatientId,
    Guid? MedicalRecordId,
    string RequestedByName,
    string? RequestingDepartment,
    RecordRequestPurpose Purpose,
    string? Details);

/// <summary>
/// Issues a request: the chart, lent out until <see cref="DueBackOn"/>, or a copy. A copy issued as documents holds
/// <see cref="Documents"/>, each made now and kept as it is handed over (BSA 2023, s.63).
/// </summary>
public sealed record IssueRecordRequest(DateOnly? DueBackOn, IReadOnlyList<RecordCopyDocument>? Documents = null);

/// <summary>A document to put in a copy: the kind and id the request's document list gives.</summary>
public sealed record RecordCopyDocument(string Kind, Guid Id);

/// <summary>
/// A chart issued is due back today or later; left empty, a week from today, and a copy not at all. A copy holds each
/// document once, and up to <see cref="MaxDocuments"/>.
/// </summary>
public sealed class IssueRecordRequestValidator : AbstractValidator<IssueRecordRequest>
{
    public const int MaxDocuments = 50;

    public IssueRecordRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.DueBackOn).GreaterThanOrEqualTo(_ => time.Today()).WithMessage("Choose today or a later day for its return.");
        RuleFor(x => x.Documents).Must(d => d is null || d.Count <= MaxDocuments)
            .WithMessage($"A copy holds up to {MaxDocuments} documents: issue the rest on another request.");
        RuleFor(x => x.Documents).Must(d => d is null || d.DistinctBy(x => (x.Kind, x.Id)).Count() == d.Count)
            .WithMessage("Choose each document once.");
        RuleForEach(x => x.Documents).ChildRules(d =>
        {
            d.RuleFor(x => x.Kind).NotEmpty().MaximumLength(64);
            d.RuleFor(x => x.Id).NotEmpty();
        });
    }
}

/// <summary>A file of a copy issued, with the SHA-256 its certificate states.</summary>
public sealed record RecordCopyFileDto(
    Guid Id, int Sequence, string DocumentKind, Guid DocumentId, string Title, DateTimeOffset DocumentDate, string FileName, long SizeBytes,
    string Sha256, DateTimeOffset ProducedAt);

/// <summary>A SHA-256 to look for among the files of the copies issued: the certificate's value, or one worked out from a file.</summary>
public sealed record VerifyRecordCopyRequest(string Sha256);

public sealed class VerifyRecordCopyRequestValidator : AbstractValidator<VerifyRecordCopyRequest>
{
    public VerifyRecordCopyRequestValidator() =>
        RuleFor(x => x.Sha256).Must(h => RecordCopyFile.NormalizeSha256(h) is not null)
            .WithMessage("Enter a SHA-256 value: 64 characters, 0-9 and A-F.");
}

/// <summary>A file issued with the SHA-256 looked for: which copy it was, of whose record, to whom, and when.</summary>
public sealed record RecordCopyMatch(
    Guid RequestId, string RequestNumber, string? Uhid, string? PatientName, RecordRequestPurpose Purpose, string RequestedByName,
    DateTimeOffset? IssuedAt, int Sequence, string Title, string FileName, long SizeBytes, string Sha256);

public sealed record CancelRecordRequest(string Reason);

public sealed record RecordRequestDto(
    Guid Id,
    string RequestNumber,
    Guid PatientId,
    string? Uhid,
    string? PatientName,
    Guid? MedicalRecordId,
    string RequestedByName,
    string? RequestingDepartment,
    RecordRequestPurpose Purpose,
    string? Details,
    bool IsPatientRequest,
    RecordRequestStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? IssuedAt,
    DateOnly? DueBackOn,
    DateTimeOffset? ReturnedAt,
    string? CancelReason,

    // A copy for the patient or a legal authority is issued within 72 hours (MCI Regulations 2002, reg. 1.3.2): when it is
    // due, and whether it went, or is still waiting, past that.
    DateTimeOffset? CopyDueAt = null,
    bool CopyLate = false,

    // The files of a copy issued as documents (BSA 2023, s.63), in the order its certificate lists them.
    IReadOnlyList<RecordCopyFileDto>? Files = null);

public sealed class CreateMedicalRecordRequestValidator : AbstractValidator<CreateMedicalRecordRequest>
{
    public CreateMedicalRecordRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x).Must(x => x.EncounterId is not null || x.AdmissionId is not null || x.Paper is not null)
            .WithName("admissionId").WithMessage("A chart tracks an admission, an encounter or a paper file.");
        RuleFor(x => x).Must(x => x.Paper is null || (x.EncounterId is null && x.AdmissionId is null && x.VisitId is null))
            .WithName("paper").WithMessage("A paper chart is of a file only: leave out the admission, encounter and visit.");
        When(x => x.Paper is not null, () =>
        {
            RuleFor(x => x.Paper!.FileNumber).NotEmpty().MaximumLength(MedicalRecord.PaperFileNumberMaxLength)
                .Matches("^[A-Za-z0-9][A-Za-z0-9/.-]*$").OverridePropertyName("paper.fileNumber")
                .WithMessage("Enter the number on the file: letters, digits, / . and -.");
            RuleFor(x => x.Paper!.EpisodeFrom).NotEmpty().OverridePropertyName("paper.episodeFrom")
                .WithMessage("Enter the visit day, or the admission day of a stay.");
            RuleFor(x => x.Paper!.EpisodeFrom).LessThanOrEqualTo(_ => time.Today()).OverridePropertyName("paper.episodeFrom")
                .WithMessage("The episode cannot be in the future.");
            // A stay's records are kept from discharge, so a stay's file says when it ended.
            RuleFor(x => x.Paper!.EpisodeTo).NotNull().When(x => x.Paper!.IsInpatient).OverridePropertyName("paper.episodeTo")
                .WithMessage("Enter the discharge day: the stay's records are kept from it.");
            RuleFor(x => x.Paper!.EpisodeTo).Must((x, to) => to is null || (to >= x.Paper!.EpisodeFrom && to <= time.Today()))
                .OverridePropertyName("paper.episodeTo").WithMessage("The discharge day is on or after the admission day, and not in the future.");
            RuleFor(x => x.Paper!.EpisodeTo).Null().When(x => !x.Paper!.IsInpatient).OverridePropertyName("paper.episodeTo")
                .WithMessage("An out-patient episode is one day: leave the discharge day empty.");
            RuleFor(x => x.Paper!.Note).SafeText(300).OverridePropertyName("paper.note");
        });
    }
}

public sealed class AddDeficiencyRequestValidator : AbstractValidator<AddDeficiencyRequest>
{
    public AddDeficiencyRequestValidator() => RuleFor(x => x.Description).NotEmpty().SafeText(500);
}

public sealed class ResolveDeficiencyRequestValidator : AbstractValidator<ResolveDeficiencyRequest>
{
    public ResolveDeficiencyRequestValidator() => RuleFor(x => x.Note).SafeText(500);
}

public sealed class ArchiveMedicalRecordRequestValidator : AbstractValidator<ArchiveMedicalRecordRequest>
{
    public ArchiveMedicalRecordRequestValidator() => RuleFor(x => x.Location).NotEmpty().MaximumLength(200)
        .WithMessage("Record where the file is stored (rack/shelf or e-archive reference).");
}

public sealed class CreateRecordRequestRequestValidator : AbstractValidator<CreateRecordRequestRequest>
{
    public CreateRecordRequestRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.RequestedByName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.RequestingDepartment).MaximumLength(128);
        RuleFor(x => x.Details).SafeText(1000);
    }
}

public sealed class CancelRecordRequestValidator : AbstractValidator<CancelRecordRequest>
{
    public CancelRecordRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
}
