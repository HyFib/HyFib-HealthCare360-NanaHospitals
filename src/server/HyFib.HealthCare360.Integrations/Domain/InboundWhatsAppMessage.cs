namespace HyFib.HealthCare360.Integrations.Domain;

/// <summary>Opt-in/opt-out keywords a patient can send (INT-WA opt-in tracking).</summary>
public enum WhatsAppKeyword
{
    None = 0,
    OptIn,
    OptOut,
}

/// <summary>
/// A message a patient sent to the hospital's WhatsApp number, received through the Cloud API webhook and kept for
/// CRM follow-up and opt-in/opt-out tracking. The sender number is kept in E.164 so staff can reply; lists show it
/// masked. <see cref="ProviderMessageId"/> is unique because Meta redelivers webhooks.
/// </summary>
[NotAudited]
public sealed class InboundWhatsAppMessage : Entity
{
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>When the patient sent it (provider timestamp).</summary>
    public DateTimeOffset? SentAt { get; set; }

    public Guid? FacilityId { get; set; }
    public string ProviderMessageId { get; set; } = string.Empty;
    public string FromPhone { get; set; } = string.Empty;
    public string? ProfileName { get; set; }
    public string MessageType { get; set; } = "text";
    public string? Text { get; set; }
    public string? MediaId { get; set; }

    /// <summary>The hospital message this one replies to (wamid), when the patient used "reply".</summary>
    public string? ContextMessageId { get; set; }

    public WhatsAppKeyword Keyword { get; set; }

    /// <summary>The hospital's WhatsApp phone-number id that received it (several numbers can share one webhook).</summary>
    public string? PhoneNumberId { get; set; }
}
