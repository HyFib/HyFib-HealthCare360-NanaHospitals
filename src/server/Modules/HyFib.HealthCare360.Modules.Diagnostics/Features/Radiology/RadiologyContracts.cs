using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;

public sealed record ImagingStudyListItem(
    Guid Id,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    PatientClass PatientClass,
    string StudyName,
    Modality Modality,
    string? BodyPart,
    OrderPriority Priority,
    ImagingStudyStatus Status,
    DateTimeOffset OrderedAt,
    DateTimeOffset? ScheduledAt,
    string? Room,
    bool HasUrgentAiFinding,
    DiagnosticReportStatus? ReportStatus,
    bool IsCriticalFinding);

public sealed record ImagingRepeatDto(Guid Id, ImageRepeatReason Reason, string? Note, DateTimeOffset RecordedAt);

public sealed record ImagingStudyDetail(
    Guid Id,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid? VisitId,
    Guid? AdmissionId,
    PatientClass PatientClass,
    string? SourceOrderNumber,
    string ServiceCode,
    string StudyName,
    Modality Modality,
    string? BodyPart,
    OrderPriority Priority,
    Guid? OrderingPractitionerId,
    string? OrderingDoctor,
    string? ClinicalNotes,
    DateTimeOffset OrderedAt,
    ImagingStudyStatus Status,
    DateTimeOffset? ScheduledAt,
    string? Room,
    string? StationAeTitle,
    DateTimeOffset? ArrivedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? AcquiredAt,
    string? Technician,
    bool ContrastUsed,
    string? ContrastAgent,
    decimal? ContrastVolumeMl,
    string? DoseNote,
    string? TechnicianNotes,
    string StudyInstanceUid,

    // False when the accession number is longer than DICOM's 16 characters: the worklist then sends the study without
    // it, with WorklistProcedureId as its procedure id, and the images are linked by StudyInstanceUid.
    bool AccessionFitsDicom,
    string WorklistProcedureId,
    DateTimeOffset? PacsLinkedAt,
    int? SeriesCount,
    int? InstanceCount,
    DateTimeOffset? ReportedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    string? CancelReason,
    string? ChargeError,

    // Set for a health-checkup package study: charged once under the package, so no per-study charge exists.
    Guid? PackageBookingId,
    int Quantity,
    string? PatientPreparation,
    IReadOnlyList<ImagingRepeatDto> Repeats,
    uint Version,

    // An ultrasound in pregnancy, and how far its PCPNDT Form F is (null: none yet).
    bool RequiresFormF = false,
    FormFStatus? FormFStatus = null);

public sealed record ScheduleStudyRequest(DateTimeOffset ScheduledAt, string? Room, string? StationAeTitle);

public sealed record AcquireStudyRequest(bool ContrastUsed, string? ContrastAgent, decimal? ContrastVolumeMl, string? DoseNote, string? Notes);

public sealed record RecordRepeatRequest(ImageRepeatReason Reason, string? Note);

public sealed record CancelStudyRequest(string Reason);

public sealed record ViewerLink(Guid StudyId, string Url, string StudyInstanceUid);

public sealed record PacsReconciliationSummary(int Checked, int Linked, IReadOnlyList<string> Messages);

/// <summary>A series of the study's images in PACS (picture series only).</summary>
public sealed record ImageSeriesDto(string SeriesInstanceUid, int? SeriesNumber, string? Modality, string? Description, string? BodyPart, int? ImageCount);

/// <summary>The study's images in PACS: its series, for thumbnails and the image viewer (IMG-02).</summary>
public sealed record StudyImagesDto(Guid StudyId, string AccessionNumber, string StudyInstanceUid, IReadOnlyList<ImageSeriesDto> Series);

/// <summary>One object of a series. <c>Frames</c> is 1 for a single picture, more for a cine loop or multi-frame image.</summary>
public sealed record ImageInstanceDto(string SopInstanceUid, int? InstanceNumber, int Frames);

/// <summary>The objects of one series, in instance-number order, for the image viewer.</summary>
public sealed record ImageSeriesDetail(Guid StudyId, ImageSeriesDto Series, IReadOnlyList<ImageInstanceDto> Instances);

public sealed record RadiologyAddendumDto(Guid Id, string Text, string? Reason, bool IsCriticalFinding, DateTimeOffset AddedAt, string? AddedBy);

public sealed record RadiologyReportDto(
    Guid Id,
    Guid ImagingStudyId,
    Guid? TemplateId,
    string? Technique,
    string? ClinicalHistory,
    string? Comparison,
    string Findings,
    string Impression,
    bool IsCriticalFinding,
    string? CriticalFindingNote,
    DiagnosticReportStatus Status,
    string? ReportedBy,
    DateTimeOffset? ReportedAt,
    string? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<RadiologyAddendumDto> Addenda,
    uint Version);

public sealed record SaveRadiologyReportRequest(
    Guid? TemplateId,
    string? Technique,
    string? ClinicalHistory,
    string? Comparison,
    string Findings,
    string Impression,
    bool IsCriticalFinding,
    string? CriticalFindingNote,
    uint? Version);

/// <summary>Sign-off; <see cref="CriticalCall"/> is required for a critical finding without a documented call.</summary>
/// <param name="Version">The version of the report the radiologist read (REL-04): 409 when it was changed since.</param>
public sealed record SignReportRequest(CriticalCallDetails? CriticalCall, uint Version, bool Release = true);

/// <param name="Version">The version of the report as it was written and read (REL-04): 409 when it was changed since.</param>
public sealed record SubmitReportRequest(uint Version);

public sealed record AddAddendumRequest(string Text, string? Reason, bool IsCriticalFinding, CriticalCallDetails? CriticalCall);

public sealed record RadiologyReportView(
    Guid ReportId,
    Guid ImagingStudyId,
    ReportLetterhead Letterhead,
    ReportPatient Patient,
    string AccessionNumber,
    string StudyName,
    Modality Modality,
    string? BodyPart,
    string? ReferredBy,
    DateTimeOffset OrderedAt,
    DateTimeOffset? AcquiredAt,
    string? Technique,
    string? ClinicalHistory,
    string? Comparison,
    string Findings,
    string Impression,
    bool IsCriticalFinding,
    DiagnosticReportStatus Status,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<ReportSignatory> Signatories,
    IReadOnlyList<RadiologyAddendumDto> Addenda,
    string? VerificationCode,
    string? VerificationPath,
    string? VerificationUrl);

public sealed record ReportTemplateDto(
    Guid Id, string Code, string Name, Modality Modality, string? BodyPart, string? Technique, string Findings, string Impression, bool IsActive, uint Version);

public sealed record SaveReportTemplateRequest(
    string Code, string Name, Modality Modality, string? BodyPart, string? Technique, string Findings, string Impression, bool IsActive, uint? Version);

public sealed record ImagingProcedureDto(
    Guid Id,
    string Code,
    string Name,
    Modality Modality,
    string BodyPart,
    Guid? ServiceItemId,
    int DurationMinutes,
    bool RequiresContrast,
    string? PatientPreparation,
    bool IsActive,
    uint Version,
    bool RequiresFormF = false);

public sealed record SaveImagingProcedureRequest(
    string Code,
    string Name,
    Modality Modality,
    string BodyPart,
    Guid? ServiceItemId,
    int DurationMinutes,
    bool RequiresContrast,
    string? PatientPreparation,
    bool IsActive,
    uint? Version,
    bool RequiresFormF = false);

public sealed record AiFindingDto(
    Guid Id,
    string Engine,
    string? EngineVersion,
    string Finding,
    decimal? Confidence,
    bool IsUrgent,
    DateTimeOffset ReceivedAt,
    AiFindingStatus Status,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote);

public sealed record SubmitAiFindingRequest(string Engine, string? EngineVersion, string Finding, decimal? Confidence, bool IsUrgent);

public sealed record ReviewAiFindingRequest(bool Acknowledge, string? Note);

public sealed record RepeatReasonStat(ImageRepeatReason Reason, int Count);

public sealed record RadiologyTatRow(
    Modality Modality,
    int Studies,
    int Reported,
    TatSummary OrderToAcquisition,
    TatSummary AcquisitionToReport,
    TatSummary OrderToReport,
    int Repeats,
    double? RepeatRatePercent);

public sealed record RadiologyTatReport(DateOnly From, DateOnly To, IReadOnlyList<RadiologyTatRow> Modalities, IReadOnlyList<RepeatReasonStat> RepeatReasons);

public sealed class ScheduleStudyRequestValidator : AbstractValidator<ScheduleStudyRequest>
{
    public ScheduleStudyRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.ScheduledAt).Must(at => at >= time.GetUtcNow().AddDays(-1)).WithMessage("The appointment cannot be in the past.");
        RuleFor(x => x.Room).MaximumLength(64);
        RuleFor(x => x.StationAeTitle).MaximumLength(16).Matches("^[A-Za-z0-9_\\-]*$").WithMessage("An AE title has at most 16 letters, digits, '-' or '_'.");
    }
}

public sealed class AcquireStudyRequestValidator : AbstractValidator<AcquireStudyRequest>
{
    public AcquireStudyRequestValidator()
    {
        RuleFor(x => x.ContrastAgent).NotEmpty().When(x => x.ContrastUsed).WithMessage("Enter the contrast agent used.");
        RuleFor(x => x.ContrastAgent).MaximumLength(100);
        RuleFor(x => x.ContrastVolumeMl).InclusiveBetween(0m, 1000m);
        RuleFor(x => x.DoseNote).SafeText(200);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class RecordRepeatRequestValidator : AbstractValidator<RecordRepeatRequest>
{
    public RecordRepeatRequestValidator()
    {
        RuleFor(x => x.Reason).IsInEnum();
        RuleFor(x => x.Note).SafeText(500);
        RuleFor(x => x.Note).NotEmpty().When(x => x.Reason == ImageRepeatReason.Other).WithMessage("Describe why the image was repeated.");
    }
}

public sealed class CancelStudyRequestValidator : AbstractValidator<CancelStudyRequest>
{
    public CancelStudyRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class SaveRadiologyReportRequestValidator : AbstractValidator<SaveRadiologyReportRequest>
{
    public SaveRadiologyReportRequestValidator()
    {
        RuleFor(x => x.Technique).SafeText(2000);
        RuleFor(x => x.ClinicalHistory).SafeText(1000);
        RuleFor(x => x.Comparison).SafeText(500);
        RuleFor(x => x.Findings).NotNull().SafeText(8000);
        RuleFor(x => x.Impression).NotNull().SafeText(4000);
        RuleFor(x => x.CriticalFindingNote).NotEmpty().When(x => x.IsCriticalFinding).WithMessage("Describe the critical finding.");
        RuleFor(x => x.CriticalFindingNote).SafeText(500);
    }
}

public sealed class SignReportRequestValidator : AbstractValidator<SignReportRequest>
{
    public SignReportRequestValidator()
    {
        RuleFor(x => x.CriticalCall!).SetValidator(new CriticalCallDetailsValidator()).When(x => x.CriticalCall is not null);
        RuleFor(x => x.Version).SeenVersion();
    }
}

public sealed class SubmitReportRequestValidator : AbstractValidator<SubmitReportRequest>
{
    public SubmitReportRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed class AddAddendumRequestValidator : AbstractValidator<AddAddendumRequest>
{
    public AddAddendumRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().SafeText(4000);
        RuleFor(x => x.Reason).SafeText(300);
        RuleFor(x => x.CriticalCall!).SetValidator(new CriticalCallDetailsValidator()).When(x => x.CriticalCall is not null);
    }
}

public sealed class SaveReportTemplateRequestValidator : AbstractValidator<SaveReportTemplateRequest>
{
    public SaveReportTemplateRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Matches("^[A-Z0-9_\\-]+$").WithMessage("Use upper-case letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Modality).IsInEnum();
        RuleFor(x => x.BodyPart).MaximumLength(100);
        RuleFor(x => x.Technique).SafeText(2000);
        RuleFor(x => x.Findings).NotEmpty().SafeText(8000);
        RuleFor(x => x.Impression).NotEmpty().SafeText(4000);
    }
}

public sealed class SaveImagingProcedureRequestValidator : AbstractValidator<SaveImagingProcedureRequest>
{
    public SaveImagingProcedureRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Matches("^[A-Z0-9_\\-]+$").WithMessage("Use upper-case letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200);
        RuleFor(x => x.Modality).IsInEnum();
        RuleFor(x => x.BodyPart).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, 240);
        RuleFor(x => x.PatientPreparation).SafeText(1000);
    }
}

public sealed class SubmitAiFindingRequestValidator : AbstractValidator<SubmitAiFindingRequest>
{
    public SubmitAiFindingRequestValidator()
    {
        RuleFor(x => x.Engine).NotEmpty().MaximumLength(100);
        RuleFor(x => x.EngineVersion).MaximumLength(32);
        RuleFor(x => x.Finding).NotEmpty().SafeText(2000);
        RuleFor(x => x.Confidence).InclusiveBetween(0m, 1m);
    }
}

public sealed class ReviewAiFindingRequestValidator : AbstractValidator<ReviewAiFindingRequest>
{
    public ReviewAiFindingRequestValidator() => RuleFor(x => x.Note).SafeText(500);
}
