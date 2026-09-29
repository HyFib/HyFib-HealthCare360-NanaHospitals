namespace HyFib.HealthCare360.Integrations.Domain;

/// <summary>Opt-in/opt-out keywords a patient can send (INT-WA opt-in tracking).</summary>
public enum WhatsAppKeyword
{
    None = 0,
    OptIn,
    OptOut,
}

/// <summary>What HC360 did about a message's STOP or START keyword.</summary>
public enum WhatsAppKeywordAction
{
    /// <summary>Nothing: no keyword, or the sender's number could not be read.</summary>
    None = 0,

    /// <summary>WhatsApp messages to the number stopped, except sign-in codes.</summary>
    OptedOut,

    /// <summary>WhatsApp messages to the number resumed.</summary>
    OptedIn,

    /// <summary>Nothing changed: a later STOP or START from the same number had already arrived.</summary>
    Superseded,
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

    /// <summary>What was done about <see cref="Keyword"/>; null for messages stored before HC360 acted on keywords.</summary>
    public WhatsAppKeywordAction? KeywordAction { get; set; }

    /// <summary>The hospital's WhatsApp phone-number id that received it (several numbers can share one webhook).</summary>
    public string? PhoneNumberId { get; set; }

    /// <summary>When the patient sent it, or when it arrived if WhatsApp gave no time.</summary>
    public DateTimeOffset RepliedAt => SentAt ?? ReceivedAt;

    /// <summary>
    /// Acts on a STOP or START: the platform stops or resumes WhatsApp messages to the number, and Engagement updates the
    /// consent of the patients registered with it, in the same transaction as this message.
    /// </summary>
    public void ApplyKeyword()
    {
        if (Keyword == WhatsAppKeyword.None || !FromPhone.StartsWith('+'))
        {
            KeywordAction = WhatsAppKeywordAction.None;
            return;
        }

        var optedOut = Keyword == WhatsAppKeyword.OptOut;
        KeywordAction = optedOut ? WhatsAppKeywordAction.OptedOut : WhatsAppKeywordAction.OptedIn;
        Raise(new WhatsAppOptOutChanged(FromPhone, optedOut, RepliedAt, ProviderMessageId, FacilityId));
    }

    /// <summary>A later STOP or START from the same number already decided; this one changes nothing.</summary>
    public void SupersedeKeyword() => KeywordAction = WhatsAppKeywordAction.Superseded;
}
