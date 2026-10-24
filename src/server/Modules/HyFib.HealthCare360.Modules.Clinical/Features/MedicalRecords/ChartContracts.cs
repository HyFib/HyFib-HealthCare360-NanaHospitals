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

public sealed record IssueRecordRequest(DateOnly? DueBackOn);

/// <summary>A chart issued is due back today or later; left empty, a week from today.</summary>
public sealed class IssueRecordRequestValidator : AbstractValidator<IssueRecordRequest>
{
    public IssueRecordRequestValidator(TimeProvider time) =>
        RuleFor(x => x.DueBackOn).GreaterThanOrEqualTo(_ => time.Today()).WithMessage("Choose today or a later day for its return.");
}

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
    string? CancelReason);

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
            RuleFor(x => x.Paper!.EpisodeFrom).LessThanOrEqualTo(_ => time.Today()).OverridePropertyName("paper.episodeFrom")
                .WithMessage("The episode cannot be in the future.");
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
