using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.EarlyWarning;

/// <param name="OpenMinutes">For an open escalation, minutes since the vital signs were taken.</param>
/// <param name="ReminderSentAt">When the ward was reminded that nobody had responded (30 minutes urgent, 15 emergency).</param>
public sealed record EscalationDto(
    Guid Id,
    Guid AdmissionId,
    Guid PatientId,
    string Uhid,
    Guid? WardId,
    string? WardName,
    string? BedLabel,
    Guid VitalSignsId,
    DateTimeOffset VitalsRecordedAt,
    int Score,
    bool AnySingleParameterThree,
    bool IsScoreComplete,
    string Summary,
    EscalationLevel Level,
    EscalationStatus Status,
    DateTimeOffset RaisedAt,
    int? OpenMinutes,
    DateTimeOffset? RespondedAt,
    string? RespondedByName,
    bool RespondedByDoctor,
    EscalationOutcome? Outcome,
    string? Response,
    int? MinutesToRespond,
    uint Version,
    DateTimeOffset? ReminderSentAt = null);

/// <summary>Who responded is the signed-in user; the time is now.</summary>
public sealed record RespondToEscalationRequest(EscalationOutcome Outcome, string Response, uint Version);

public sealed class RespondToEscalationRequestValidator : AbstractValidator<RespondToEscalationRequest>
{
    public RespondToEscalationRequestValidator()
    {
        RuleFor(x => x.Outcome).IsInEnum();
        RuleFor(x => x.Response).NotEmpty().SafeText(1000);
    }
}
