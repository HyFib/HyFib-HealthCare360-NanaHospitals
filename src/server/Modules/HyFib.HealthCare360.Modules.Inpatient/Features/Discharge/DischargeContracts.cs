using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;

public sealed record InitiateDischargeRequest(DateOnly? ExpectedDischargeDate, string? Notes);

public sealed record RevokeDischargeRequest(string Reason);

/// <summary>
/// How the stay ends. By type (maker-checker, slice 8): LAMA and DAMA go with <paramref name="AgainstAdvice"/> (who signed,
/// the doctor who explained the risks, a witness's confirmation, the scanned form); Absconded with
/// <paramref name="Absconded"/> (last seen, who searched, police and relatives told) and waits for the nurse in charge;
/// Expired needs the patient's death certificate confirmed by a second doctor, or, for a medico-legal death whose
/// certificate waits for the post-mortem (<paramref name="CertificatePendingPostMortem"/>), a second doctor's approval.
/// </summary>
public sealed record CompleteDischargeRequest(
    DischargeType DischargeType,
    DateTimeOffset? DischargedAt,
    string? Notes,
    bool NotifyPatient = true,
    AgainstAdviceRequest? AgainstAdvice = null,
    AbscondedReport? Absconded = null,
    bool CertificatePendingPostMortem = false);

/// <summary>The stay closed (<paramref name="Discharged"/>), or the request for a second person to approve closing it (<paramref name="Asked"/>, answered 202).</summary>
public sealed record DischargeOutcome(DischargeResult? Discharged, ApprovalRequestDto? Asked);

/// <summary>
/// The patient's death certificate as the ward sees it when closing the stay as expired: none yet, waiting for a second
/// doctor's confirmation, or enough to close the stay; and what the ward is told.
/// </summary>
public sealed record DischargeDeathCertificateDto(
    Guid AdmissionId,
    Guid? DeathId,
    string? SerialNumber,
    bool Settled,
    bool ForThisStay,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmationWaitingSince,
    bool MedicoLegalStay,
    string Status);

/// <summary>A stay's closing waiting for a second person (Absconded, or Expired pending post-mortem): the request, who asked and when.</summary>
public sealed record PendingDischarge(Guid ApprovalId, DischargeType Type, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>How a closed stay ended, as declared: the against-advice form, the abscondence report, the death certificate, the dues left, and who approved.</summary>
public sealed record DischargeDeclarationDto(
    DischargeType Type,
    string? SignedBy,
    string? RisksExplainedBy,
    string? WitnessName,
    Guid? SignedFormFileId,
    DateTimeOffset? LastSeenAt,
    string? SearchedBy,
    bool PoliceInformed,
    string? PoliceStation,
    string? PoliceReference,
    bool RelativesInformed,
    string? RelativeInformed,
    string? DeathCertificateNumber,
    bool CertificatePendingPostMortem,
    decimal? DuesOutstanding,
    string RecordedBy,
    string? ApprovedBy);

public sealed record DischargeClearanceDto(Guid AdmissionId, bool Cleared, decimal Outstanding, string? Reason);

public sealed record DischargeResult(Guid AdmissionId, AdmissionStatus Status, DateTimeOffset? DischargeInitiatedAt, DateTimeOffset? DischargedAt, int ChargesPosted);

public sealed record PendingDischargeItem(
    Guid AdmissionId,
    string AdmissionNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    string? WardName,
    string? BedLabel,
    string? ConsultantName,
    DateTimeOffset DischargeInitiatedAt,
    DateOnly? ExpectedDischargeDate,
    int WaitingMinutes);

public sealed class InitiateDischargeRequestValidator : AbstractValidator<InitiateDischargeRequest>
{
    public InitiateDischargeRequestValidator() => RuleFor(x => x.Notes).SafeText(2000);
}

public sealed class RevokeDischargeRequestValidator : AbstractValidator<RevokeDischargeRequest>
{
    public RevokeDischargeRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class CompleteDischargeRequestValidator : AbstractValidator<CompleteDischargeRequest>
{
    public CompleteDischargeRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.DischargeType).IsInEnum();
        RuleFor(x => x.DischargedAt)
            .Must(at => at is null || (at <= time.GetUtcNow().AddMinutes(5) && at >= time.GetUtcNow().AddHours(-48)))
            .WithMessage("The discharge time must be within the last 48 hours and not in the future.");
        RuleFor(x => x.Notes).SafeText(2000);
        RuleFor(x => x.Notes).NotEmpty().When(x => DischargeRules.RequiresNotes(x.DischargeType) || x.CertificatePendingPostMortem)
            .WithMessage("Record who was informed and which form was signed (LAMA/DAMA/absconded is a medico-legal event).");
        RuleFor(x => x.AgainstAdvice).NotNull().When(x => DischargeRules.IsAgainstAdvice(x.DischargeType))
            .WithMessage("Record who signed the form (or that the patient refused to sign), the doctor who explained the risks and a witness who confirms with their own user name and password.");
        RuleFor(x => x.AgainstAdvice!).SetValidator(new AgainstAdviceRequestValidator()).When(x => x.AgainstAdvice is not null);
        RuleFor(x => x.Absconded).NotNull().When(x => x.DischargeType == DischargeType.Absconded)
            .WithMessage("Record when the patient was last seen, who searched, and whether the police and relatives were told.");
        RuleFor(x => x.Absconded!).SetValidator(new AbscondedReportValidator()).When(x => x.Absconded is not null);
        RuleFor(x => x.CertificatePendingPostMortem).Equal(false).When(x => x.DischargeType != DischargeType.Expired)
            .WithMessage("Only a death waits for the post-mortem.");
    }
}

public sealed class AbscondedReportValidator : AbstractValidator<AbscondedReport>
{
    public AbscondedReportValidator()
    {
        RuleFor(x => x.LastSeenAt).NotEmpty().WithMessage("Enter when the patient was last seen.");
        RuleFor(x => x.SearchedBy).NotEmpty().WithMessage("Say who searched for the patient.").SafeText(300);
        RuleFor(x => x.PoliceStation).SafeText(200);
        RuleFor(x => x.PoliceReference).SafeText(100);
        RuleFor(x => x.RelativeInformed).SafeText(200);
    }
}
