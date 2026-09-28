using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.Cardiology;

public sealed record CardiologyStudyListItem(
    Guid Id,
    string AccessionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    PatientClass PatientClass,
    string StudyName,
    CardiologyStudyType StudyType,
    OrderPriority Priority,
    CardiologyStudyStatus Status,
    DateTimeOffset OrderedAt,
    DateTimeOffset? ScheduledAt,
    DiagnosticReportStatus? ReportStatus,
    bool IsCriticalFinding);

public sealed record DeviceFileDto(Guid Id, string? FileName, string? ContentType, long? SizeBytes);

public sealed record CardiologyReportDto(
    Guid Id,
    CardiologyStudyType StudyType,
    EcgFindings Ecg,
    EchoFindings Echo,
    TmtFindings Tmt,
    HolterFindings Holter,
    AbpmFindings Abpm,
    string? Conclusion,
    string? Recommendations,
    bool IsCriticalFinding,
    string? CriticalFindingNote,
    DiagnosticReportStatus Status,
    string? ReportedBy,
    DateTimeOffset? ReportedAt,
    string? VerifiedBy,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    uint Version);

public sealed record CardiologyStudyDetail(
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
    CardiologyStudyType StudyType,
    OrderPriority Priority,
    Guid? OrderingPractitionerId,
    string? OrderingDoctor,
    string? ClinicalNotes,
    DateTimeOffset OrderedAt,
    CardiologyStudyStatus Status,
    DateTimeOffset? ScheduledAt,
    string? Room,
    DateTimeOffset? StartedAt,
    DateTimeOffset? AcquiredAt,
    string? Technician,
    string? DeviceName,
    string? TechnicianNotes,
    DateTimeOffset? ReportedAt,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    string? CancelReason,
    string? ChargeError,

    // Set for a health-checkup package study: charged once under the package, so no per-study charge exists.
    Guid? PackageBookingId,
    int Quantity,
    IReadOnlyList<DeviceFileDto> Files,
    CardiologyReportDto? Report,
    uint Version);

public sealed record ScheduleCardiologyRequest(DateTimeOffset ScheduledAt, string? Room);

public sealed record StartCardiologyRequest(string? DeviceName);

/// <summary>Device output already uploaded through <c>POST /api/v1/files</c> (PDF tracings, echo images).</summary>
public sealed record AttachDeviceFilesRequest(IReadOnlyList<Guid> FileIds);

public sealed record CompleteCardiologyRequest(string? Notes);

public sealed record CancelCardiologyRequest(string Reason);

public sealed record SaveCardiologyReportRequest(
    EcgFindings? Ecg,
    EchoFindings? Echo,
    TmtFindings? Tmt,
    HolterFindings? Holter,
    AbpmFindings? Abpm,
    string? Conclusion,
    string? Recommendations,
    bool IsCriticalFinding,
    string? CriticalFindingNote,
    uint? Version);

/// <summary>Cardiologist sign-off; <see cref="CriticalCall"/> is required for a critical finding without a documented call.</summary>
public sealed record SignCardiologyReportRequest(CriticalCallDetails? CriticalCall, bool Release = true);

public sealed record CardiologyReportView(
    Guid ReportId,
    Guid CardiologyStudyId,
    ReportLetterhead Letterhead,
    ReportPatient Patient,
    string AccessionNumber,
    string StudyName,
    CardiologyStudyType StudyType,
    string? ReferredBy,
    DateTimeOffset OrderedAt,
    DateTimeOffset? AcquiredAt,
    EcgFindings? Ecg,
    EchoFindings? Echo,
    TmtFindings? Tmt,
    HolterFindings? Holter,
    AbpmFindings? Abpm,
    string? Conclusion,
    string? Recommendations,
    bool IsCriticalFinding,
    DiagnosticReportStatus Status,
    DateTimeOffset? VerifiedAt,
    DateTimeOffset? ReleasedAt,
    IReadOnlyList<ReportSignatory> Signatories,
    IReadOnlyList<DeviceFileDto> Files,
    string? VerificationCode,
    string? VerificationPath,
    string? VerificationUrl);

public sealed class ScheduleCardiologyRequestValidator : AbstractValidator<ScheduleCardiologyRequest>
{
    public ScheduleCardiologyRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.ScheduledAt).Must(at => at >= time.GetUtcNow().AddDays(-1)).WithMessage("The appointment cannot be in the past.");
        RuleFor(x => x.Room).MaximumLength(64);
    }
}

public sealed class StartCardiologyRequestValidator : AbstractValidator<StartCardiologyRequest>
{
    public StartCardiologyRequestValidator() => RuleFor(x => x.DeviceName).MaximumLength(100);
}

public sealed class AttachDeviceFilesRequestValidator : AbstractValidator<AttachDeviceFilesRequest>
{
    public AttachDeviceFilesRequestValidator()
    {
        RuleFor(x => x.FileIds).NotEmpty().Must(f => f.Count <= 20).WithMessage("At most 20 files at a time.");
        RuleForEach(x => x.FileIds).NotEmpty();
    }
}

public sealed class SignCardiologyReportRequestValidator : AbstractValidator<SignCardiologyReportRequest>
{
    public SignCardiologyReportRequestValidator() =>
        RuleFor(x => x.CriticalCall!).SetValidator(new CriticalCallDetailsValidator()).When(x => x.CriticalCall is not null);
}

public sealed class CompleteCardiologyRequestValidator : AbstractValidator<CompleteCardiologyRequest>
{
    public CompleteCardiologyRequestValidator() => RuleFor(x => x.Notes).SafeText(1000);
}

public sealed class CancelCardiologyRequestValidator : AbstractValidator<CancelCardiologyRequest>
{
    public CancelCardiologyRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class SaveCardiologyReportRequestValidator : AbstractValidator<SaveCardiologyReportRequest>
{
    public SaveCardiologyReportRequestValidator()
    {
        RuleFor(x => x.Conclusion).SafeText(2000);
        RuleFor(x => x.Recommendations).SafeText(1000);
        RuleFor(x => x.CriticalFindingNote).NotEmpty().When(x => x.IsCriticalFinding).WithMessage("Describe the critical finding.");
        RuleFor(x => x.CriticalFindingNote).SafeText(500);

        When(x => x.Ecg is not null, () =>
        {
            RuleFor(x => x.Ecg!.HeartRate).InclusiveBetween(10, 350);
            RuleFor(x => x.Ecg!.PrInterval).InclusiveBetween(40, 600);
            RuleFor(x => x.Ecg!.QrsDuration).InclusiveBetween(20, 400);
            RuleFor(x => x.Ecg!.QtInterval).InclusiveBetween(100, 900);
            RuleFor(x => x.Ecg!.QtcInterval).InclusiveBetween(100, 900);
            RuleFor(x => x.Ecg!.Rhythm).MaximumLength(100);
            RuleFor(x => x.Ecg!.Axis).MaximumLength(64);
            RuleFor(x => x.Ecg!.StTChanges).SafeText(500);
            RuleFor(x => x.Ecg!.Interpretation).SafeText(2000);
        });

        When(x => x.Echo is not null, () =>
        {
            RuleFor(x => x.Echo!.Lvef).InclusiveBetween(1m, 95m);
            RuleFor(x => x.Echo!.LeftVentricle).SafeText(500);
            RuleFor(x => x.Echo!.RightVentricle).SafeText(500);
            RuleFor(x => x.Echo!.LeftAtrium).SafeText(300);
            RuleFor(x => x.Echo!.RightAtrium).SafeText(300);
            RuleFor(x => x.Echo!.Valves).SafeText(1000);
            RuleFor(x => x.Echo!.Rwma).SafeText(500);
            RuleFor(x => x.Echo!.DiastolicFunction).SafeText(300);
            RuleFor(x => x.Echo!.Pericardium).SafeText(300);
            RuleFor(x => x.Echo!.Conclusion).SafeText(2000);
        });

        When(x => x.Tmt is not null, () =>
        {
            RuleFor(x => x.Tmt!.Protocol).MaximumLength(64);
            RuleFor(x => x.Tmt!.DurationSeconds).InclusiveBetween(0, 3600);
            RuleFor(x => x.Tmt!.Mets).InclusiveBetween(0m, 30m);
            RuleFor(x => x.Tmt!.MaxHeartRate).InclusiveBetween(30, 250);
            RuleFor(x => x.Tmt!.TargetHeartRatePercent).InclusiveBetween(0, 150);
            RuleFor(x => x.Tmt!.BpResponse).SafeText(300);
            RuleFor(x => x.Tmt!.Symptoms).SafeText(300);
            RuleFor(x => x.Tmt!.StChanges).SafeText(500);
            RuleFor(x => x.Tmt!.ReasonForTermination).SafeText(300);
            RuleFor(x => x.Tmt!.Result).IsInEnum();
        });

        When(x => x.Holter is not null, () =>
        {
            RuleFor(x => x.Holter!.DurationHours).InclusiveBetween(1, 336);
            RuleFor(x => x.Holter!.MinHeartRate).InclusiveBetween(10, 350);
            RuleFor(x => x.Holter!.MeanHeartRate).InclusiveBetween(10, 350);
            RuleFor(x => x.Holter!.MaxHeartRate).InclusiveBetween(10, 350);
            RuleFor(x => x.Holter!.VentricularEctopics).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Holter!.SupraventricularEctopics).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Holter!.Pauses).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Holter!.LongestPause).MaximumLength(64);
            RuleFor(x => x.Holter!.Arrhythmias).SafeText(1000);
            RuleFor(x => x.Holter!.StChanges).SafeText(500);
        });

        When(x => x.Abpm is not null, () =>
        {
            RuleFor(x => x.Abpm!.DaytimeMeanSystolic).InclusiveBetween(40, 300);
            RuleFor(x => x.Abpm!.DaytimeMeanDiastolic).InclusiveBetween(20, 200);
            RuleFor(x => x.Abpm!.NighttimeMeanSystolic).InclusiveBetween(40, 300);
            RuleFor(x => x.Abpm!.NighttimeMeanDiastolic).InclusiveBetween(20, 200);
            RuleFor(x => x.Abpm!.DippingPattern).MaximumLength(64);
        });
    }
}
