using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Telehealth;

public sealed record IceServerDto(IReadOnlyList<string> Urls, string? Username, string? Credential)
{
    public static IceServerDto From(IceServer server) => new(server.Urls, server.Username, server.Credential);
}

/// <summary>Everything a client needs before opening the call (TEL-06).</summary>
public sealed record TelehealthConfig(IReadOnlyList<IceServerDto> IceServers, int EarlyJoinMinutes, string HubPath);

public sealed record TelehealthSessionListItem(
    Guid Id,
    Guid? AppointmentId,
    string? AppointmentNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid PractitionerId,
    string PractitionerName,
    TelehealthStatus Status,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset? PatientWaitingSince,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    Guid? VisitId,
    bool LinkSent);

public sealed record TelehealthParticipantDto(
    Guid Id,
    TelehealthRole Role,
    string DisplayName,
    DateTimeOffset JoinedAt,
    DateTimeOffset? AdmittedAt,
    DateTimeOffset? LeftAt,
    bool AudioOnly,
    int? WorstRoundTripMs,
    decimal? MaxPacketLossPercent,
    int? LowestBitrateKbps);

public sealed record TelehealthSessionDetail(
    Guid Id,
    Guid FacilityId,
    Guid? AppointmentId,
    string? AppointmentNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? PhoneMasked,
    Guid PractitionerId,
    string PractitionerName,
    Guid? DepartmentId,
    Guid? VisitId,
    TelehealthStatus Status,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset? PatientWaitingSince,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    DateTimeOffset? LinkSentAt,
    int LinkSendCount,
    DateTimeOffset? JoinTokenExpiresAt,
    bool RecordingConsent,
    DateTimeOffset? RecordingConsentAt,
    bool AudioOnlyUsed,
    string? QualityNotes,
    string? CancelReason,
    int MessageCount,
    IReadOnlyList<TelehealthParticipantDto> Participants,
    uint Version);

public sealed record TelehealthMessageDto(Guid Id, TelehealthRole SenderRole, string SenderName, string Text, DateTimeOffset SentAt)
{
    public static TelehealthMessageDto From(TelehealthMessage message) =>
        new(message.Id, message.SenderRole, message.SenderName, message.Text, message.SentAt);
}

/// <summary>Creates a session for a telehealth appointment, or an ad-hoc one (patient, doctor, time).</summary>
public sealed record CreateTelehealthSessionRequest(
    Guid? AppointmentId,
    Guid? PatientId,
    Guid? PractitionerId,
    DateTimeOffset? ScheduledStartAt,
    int? DurationMinutes,
    bool SendLink = true);

/// <summary>The patient's join link, returned to staff so it can be read out or re-sent if SMS/WhatsApp fails.</summary>
public sealed record TelehealthLinkIssued(Guid SessionId, string JoinUrl, DateTimeOffset? ExpiresAt, bool Sent);

public sealed record EndTelehealthSessionRequest(string? QualityNotes);

public sealed record CancelTelehealthSessionRequest(string Reason);

public sealed record RecordingConsentRequest(bool Granted);

public sealed record PostTelehealthMessageRequest(string Text);

public sealed record PortalTelehealthSession(
    Guid Id,
    Guid PatientId,
    string PatientName,
    Guid PractitionerId,
    string PractitionerName,
    string? AppointmentNumber,
    TelehealthStatus Status,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    DateTimeOffset JoinOpensAt,
    bool CanJoinNow);

/// <summary>What the portal needs to open the call screen; the patient then joins the hub with their portal token.</summary>
public sealed record TelehealthJoinInfo(
    Guid SessionId,
    TelehealthStatus Status,
    DateTimeOffset ScheduledStartAt,
    DateTimeOffset ScheduledEndAt,
    string PractitionerName,
    string? DepartmentName,
    string PatientName,
    bool CanJoinNow,
    DateTimeOffset JoinOpensAt,
    string HubPath,
    IReadOnlyList<IceServerDto> IceServers);

// ---------------------------------------------------------------- signalling hub messages

public sealed record WaitingParticipant(Guid ParticipantId, TelehealthRole Role, string DisplayName, DateTimeOffset JoinedAt);

/// <summary>Returned by every hub join method.</summary>
public sealed record TelehealthJoinResult(
    Guid SessionId,
    Guid ParticipantId,
    TelehealthRole Role,
    TelehealthStatus Status,
    bool Admitted,
    DateTimeOffset ScheduledStartAt,
    string PractitionerName,
    string PatientName,
    IReadOnlyList<IceServerDto> IceServers,
    IReadOnlyList<WaitingParticipant> Waiting,
    IReadOnlyList<TelehealthMessageDto> RecentMessages);

/// <summary>Periodic WebRTC statistics from a client (getStats), used for TEL-05 connection quality.</summary>
public sealed record ConnectionQualitySample(int? RoundTripMs, decimal? PacketLossPercent, int? BitrateKbps, bool AudioOnly);

public sealed class CreateTelehealthSessionRequestValidator : AbstractValidator<CreateTelehealthSessionRequest>
{
    public CreateTelehealthSessionRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty().When(x => x.AppointmentId is null).WithMessage("Choose a patient or an appointment.");
        RuleFor(x => x.PractitionerId).NotEmpty().When(x => x.AppointmentId is null).WithMessage("Choose a doctor.");
        RuleFor(x => x.ScheduledStartAt).NotNull().When(x => x.AppointmentId is null).WithMessage("Choose the consultation time.");
        RuleFor(x => x.DurationMinutes).InclusiveBetween(5, 120);
    }
}

public sealed class EndTelehealthSessionRequestValidator : AbstractValidator<EndTelehealthSessionRequest>
{
    public EndTelehealthSessionRequestValidator() => RuleFor(x => x.QualityNotes).SafeText(500);
}

public sealed class CancelTelehealthSessionRequestValidator : AbstractValidator<CancelTelehealthSessionRequest>
{
    public CancelTelehealthSessionRequestValidator() => RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter a reason.").SafeText(300);
}

public sealed class PostTelehealthMessageRequestValidator : AbstractValidator<PostTelehealthMessageRequest>
{
    public PostTelehealthMessageRequestValidator() => RuleFor(x => x.Text).NotEmpty().SafeText(TelehealthMessage.MaxLength);
}
