using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Patients.Features.Patients;

// ------------------------------------------------------------------ staff

/// <summary>Sends a link to the patient portal to the patient's registered number, by SMS, WhatsApp or both.</summary>
public sealed record SendPortalLinkRequest(IReadOnlyList<NotificationChannel> Channels);

/// <summary>
/// The link sent: the channels it went on (SMS only when WhatsApp was asked for too but the number replied STOP there,
/// which <paramref name="Notice"/> then says), until when it works, and the number it went to, masked.
/// </summary>
public sealed record PortalLinkSent(Guid Id, IReadOnlyList<NotificationChannel> Channels, DateTimeOffset ExpiresAt, string MaskedPhone, string? Notice);

/// <summary>One link sent to the patient. Neither the link nor its token's hash is ever shown.</summary>
public sealed record PortalLinkDto(
    Guid Id,
    DateTimeOffset SentAt,
    string? SentByName,
    IReadOnlyList<NotificationChannel> Channels,
    string MaskedPhone,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? OpenedAt,
    PortalLinkState State,
    DateTimeOffset? RevokedAt,
    string? RevokedReason);

/// <summary>Why no link can be sent now, by its code (<c>portal.link_sensitive</c>) and in words.</summary>
public sealed record PortalLinkRefusal(string Code, string Message);

/// <summary>
/// The links sent to the patient (the latest <c>5</c>), the number a new one would go to (masked), and, for staff who may
/// send one, why none can be sent now (<paramref name="CannotSend"/>, null when one can).
/// </summary>
public sealed record PatientPortalLinksDto(string? MaskedPhone, PortalLinkRefusal? CannotSend, bool WhatsAppStopped, IReadOnlyList<PortalLinkDto> Links);

// ------------------------------------------------------------------ public (the link's page in the portal)

/// <summary>What the link's page shows before the patient asks for a code: the hospital, and the number masked.</summary>
public sealed record PublicPortalLinkDto(string Hospital, string MaskedPhone, DateTimeOffset ExpiresAt);

public sealed class SendPortalLinkRequestValidator : AbstractValidator<SendPortalLinkRequest>
{
    public SendPortalLinkRequestValidator() =>
        RuleFor(x => x.Channels).Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Choose SMS, WhatsApp or both.")
            .Must(channels => channels.Count <= 2 && channels.Distinct().Count() == channels.Count)
            .WithMessage("Choose each of SMS and WhatsApp at most once.")
            .Must(channels => channels.All(c => c is NotificationChannel.Sms or NotificationChannel.WhatsApp))
            .WithMessage("A portal link goes by SMS or WhatsApp.");
}
