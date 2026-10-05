using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;

public sealed record AdmitPatientRequest(
    Guid PatientId,
    Guid BedId,
    AdmissionType Type,
    AdmissionSource Source,
    Guid? SourceVisitId,
    Guid? DepartmentId,
    Guid? AdmittingPractitionerId,
    Guid ConsultantPractitionerId,
    string? ProvisionalDiagnosis,
    string? ReferredBy,
    PayerType PayerType,
    Guid? PayerId,
    string? PolicyNumber,
    bool IsMlc,
    string? MlcNumber,
    DateOnly? ExpectedDischargeDate,
    string? AttendantName,
    string? AttendantRelation,
    string? AttendantPhone,
    DateTimeOffset? AdmittedAt,
    string? Notes,
    bool NotifyPatient = true);

public sealed record AdmissionCreated(Guid Id, string AdmissionNumber, Guid VisitId, string VisitNumber, Guid BedId, string BedLabel);

/// <summary>
/// Editable admission details. The payer is fixed at admission (Billing opened the IP account with it); a change of
/// payer is handled by the insurance desk in Billing. <see cref="PolicyNumber"/>: null leaves it, empty removes it.
/// </summary>
public sealed record UpdateAdmissionRequest(
    Guid? DepartmentId,
    Guid ConsultantPractitionerId,
    string? ProvisionalDiagnosis,
    DateOnly? ExpectedDischargeDate,
    string? PolicyNumber,
    bool IsMlc,
    string? MlcNumber,
    string? AttendantName,
    string? AttendantRelation,
    string? AttendantPhone,
    string? Notes,
    uint Version);

public sealed record TransferPatientRequest(Guid ToBedId, string Reason, Guid? ConsultantPractitionerId, Guid? DepartmentId, DateTimeOffset? TransferredAt);

public sealed record CancelAdmissionRequest(string Reason);

public sealed record AdmissionListItem(
    Guid Id,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    AdmissionStatus Status,
    AdmissionType Type,
    DateTimeOffset AdmittedAt,
    DateTimeOffset? DischargedAt,
    Guid? WardId,
    string? WardName,
    Guid? BedId,
    string? BedLabel,
    Guid? ConsultantPractitionerId,
    string? ConsultantName,
    int LengthOfStayDays,
    PayerType PayerType,
    bool IsMlc);

public sealed record AdmissionDetail(
    Guid Id,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid VisitId,
    string? VisitNumber,
    Guid? SourceVisitId,
    AdmissionType Type,
    AdmissionSource Source,
    AdmissionStatus Status,
    DateTimeOffset AdmittedAt,
    Guid? AdmittingPractitionerId,
    string? AdmittingPractitionerName,
    Guid? ConsultantPractitionerId,
    string? ConsultantName,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? WardId,
    string? WardName,
    Guid? BedId,
    string? BedLabel,
    string? ProvisionalDiagnosis,
    string? ReferredBy,
    PayerType PayerType,
    Guid? PayerId,
    string? PolicyNumberMasked,
    bool IsMlc,
    string? MlcNumber,
    DateOnly? ExpectedDischargeDate,
    string? AttendantName,
    string? AttendantRelation,
    string? AttendantPhone,
    string? Notes,
    int LengthOfStayDays,
    DateTimeOffset? DischargeInitiatedAt,
    DischargeType? DischargeType,
    DateTimeOffset? DischargedAt,
    string? DischargeNotes,
    DateTimeOffset? CancelledAt,
    string? CancelReason,
    uint Version);

/// <summary>
/// What an in-patient's wristband shows (FO-03): who, the UHID to scan, the admission and the bed, and what the chart
/// says about allergies, which decides the band's allergy mark (ALLERGY, none, or ALLERGIES NOT RECORDED). The allergies
/// themselves are not included.
/// </summary>
public sealed record WristbandDto(
    Guid AdmissionId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string AdmissionNumber,
    string? WardName,
    string? BedLabel,
    AllergyRecordingStatus Allergies);

/// <summary>One stay in a patient's admission history.</summary>
public sealed record PatientAdmissionItem(
    Guid Id,
    string AdmissionNumber,
    AdmissionStatus Status,
    AdmissionType Type,
    DateTimeOffset AdmittedAt,
    DateTimeOffset? DischargedAt,
    Guid? WardId,
    string? WardName,
    Guid? ConsultantPractitionerId,
    string? ConsultantName,
    DischargeType? DischargeType,
    int LengthOfStayDays);

public sealed record BedAssignmentDto(
    Guid Id,
    Guid BedId,
    string BedLabel,
    Guid WardId,
    string WardName,
    BedAssignmentReason Reason,
    string? TransferReason,
    DateTimeOffset FromAt,
    DateTimeOffset? ToAt);

public sealed record ConsultantVisitDto(
    Guid Id,
    Guid PractitionerId,
    string? PractitionerName,
    DateTimeOffset VisitedAt,
    bool IsCriticalCare,
    string ServiceCode,
    string? Notes,
    bool IsCharged,
    string? ChargeError);

public sealed record RecordConsultantVisitRequest(Guid? PractitionerId, DateTimeOffset? VisitedAt, string? Notes);

public sealed record CancelConsultantVisitRequest(string Reason);

public sealed record DailyChargeDto(
    DateOnly ChargeDate,
    DailyChargeKind Kind,
    Guid ServiceItemId,
    string? ServiceCode,
    string SourceReference,
    Guid ChargeId,
    DateTimeOffset PostedAt,
    DateTimeOffset? CancelledAt);

internal static class AdmissionRules
{
    public const string PolicyPattern = "^[A-Za-z0-9/ -]*$";
    public static readonly TimeSpan BackdateWindow = TimeSpan.FromDays(3);
    public static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    public static bool WithinWindow(DateTimeOffset? at, DateTimeOffset now, TimeSpan back) =>
        at is null || (at <= now + ClockTolerance && at >= now - back);
}

public sealed class AdmitPatientRequestValidator : AbstractValidator<AdmitPatientRequest>
{
    public AdmitPatientRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.BedId).NotEmpty().WithMessage("Choose a bed.");
        RuleFor(x => x.ConsultantPractitionerId).NotEmpty().WithMessage("Choose the consultant in charge.");
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.PayerType).IsInEnum();
        RuleFor(x => x.ProvisionalDiagnosis).SafeText(500);
        RuleFor(x => x.ReferredBy).SafeText(200);
        RuleFor(x => x.ReferredBy).NotEmpty().When(x => x.Source == AdmissionSource.Referral).WithMessage("Enter the referring doctor or hospital.");
        RuleFor(x => x.PolicyNumber).MaximumLength(64).Matches(AdmissionRules.PolicyPattern).WithMessage("Use letters, digits, spaces, '/' or '-'.");
        RuleFor(x => x.MlcNumber).MaximumLength(64);
        RuleFor(x => x.AttendantName).MaximumLength(200);
        RuleFor(x => x.AttendantRelation).MaximumLength(64);
        RuleFor(x => x.AttendantPhone).PhoneNumber();
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.AdmittedAt)
            .Must(at => AdmissionRules.WithinWindow(at, time.GetUtcNow(), AdmissionRules.BackdateWindow))
            .WithMessage("The admission time must be within the last 3 days and not in the future.");
        RuleFor(x => x.ExpectedDischargeDate)
            .Must(d => d is null || d >= DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).AddDays(-1))
            .WithMessage("The expected discharge date cannot be in the past.");
    }
}

public sealed class UpdateAdmissionRequestValidator : AbstractValidator<UpdateAdmissionRequest>
{
    public UpdateAdmissionRequestValidator()
    {
        RuleFor(x => x.ConsultantPractitionerId).NotEmpty().WithMessage("Choose the consultant in charge.");
        RuleFor(x => x.ProvisionalDiagnosis).SafeText(500);
        RuleFor(x => x.PolicyNumber).MaximumLength(64).Matches(AdmissionRules.PolicyPattern).WithMessage("Use letters, digits, spaces, '/' or '-'.");
        RuleFor(x => x.MlcNumber).MaximumLength(64);
        RuleFor(x => x.AttendantName).MaximumLength(200);
        RuleFor(x => x.AttendantRelation).MaximumLength(64);
        RuleFor(x => x.AttendantPhone).PhoneNumber();
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class TransferPatientRequestValidator : AbstractValidator<TransferPatientRequest>
{
    public TransferPatientRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.ToBedId).NotEmpty().WithMessage("Choose the destination bed.");
        RuleFor(x => x.Reason).NotEmpty().SafeText(300);
        RuleFor(x => x.TransferredAt)
            .Must(at => AdmissionRules.WithinWindow(at, time.GetUtcNow(), TimeSpan.FromHours(24)))
            .WithMessage("The transfer time must be within the last 24 hours and not in the future.");
    }
}

public sealed class CancelAdmissionRequestValidator : AbstractValidator<CancelAdmissionRequest>
{
    public CancelAdmissionRequestValidator() =>
        RuleFor(x => x.Reason).NotEmpty().MinimumLength(5).SafeText(300).WithMessage("Explain why the admission is being cancelled.");
}

public sealed class RecordConsultantVisitRequestValidator : AbstractValidator<RecordConsultantVisitRequest>
{
    public RecordConsultantVisitRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.VisitedAt)
            .Must(at => AdmissionRules.WithinWindow(at, time.GetUtcNow(), TimeSpan.FromHours(48)))
            .WithMessage("The visit time must be within the last 48 hours and not in the future.");
        RuleFor(x => x.Notes).SafeText(1000);
    }
}

public sealed class CancelConsultantVisitRequestValidator : AbstractValidator<CancelConsultantVisitRequest>
{
    public CancelConsultantVisitRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
