using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;

public sealed record InitiateDischargeRequest(DateOnly? ExpectedDischargeDate, string? Notes);

public sealed record RevokeDischargeRequest(string Reason);

public sealed record CompleteDischargeRequest(DischargeType DischargeType, DateTimeOffset? DischargedAt, string? Notes, bool NotifyPatient = true);

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
        RuleFor(x => x.Notes).NotEmpty().When(x => DischargeRules.RequiresNotes(x.DischargeType))
            .WithMessage("Record who was informed and which form was signed (LAMA/DAMA/absconded is a medico-legal event).");
    }
}
