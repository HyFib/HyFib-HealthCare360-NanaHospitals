namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>The channels a portal link went on (both, when staff chose both and WhatsApp was not stopped).</summary>
[Flags]
public enum PortalLinkChannels
{
    None = 0,
    Sms = 1,
    WhatsApp = 2,
}

/// <summary>Where a portal link stands, as the patient's record shows it to staff.</summary>
public enum PortalLinkState
{
    /// <summary>Sent and not used yet.</summary>
    Active = 0,

    /// <summary>Used to ask for a sign-in code at least once; it keeps working until it expires.</summary>
    Opened,

    Expired,

    /// <summary>A newer link replaced it, or the patient's number changed (<see cref="PortalLink.RevokedReason"/>).</summary>
    Revoked,
}

/// <summary>
/// A link to the patient portal that staff send to the mobile number registered for the patient, by SMS, WhatsApp or
/// both, so the patient can sign in without typing the number (PP-01). The link is not a credential: opening it only
/// lets the patient ask for a sign-in code, which goes to the same registered number, and that code signs them in as
/// the portal's own sign-in does. Only the SHA-256 hash of its token is kept, so whoever reads the database or the audit
/// trail cannot make a link. It works for <see cref="Validity"/>, until a newer link replaces it, and only while the
/// patient's record still has the number it was sent to (<see cref="Opens"/>).
/// </summary>
public sealed class PortalLink : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(7);

    /// <summary>The most links one patient is sent in <see cref="ThrottleWindow"/>: each one is a message to their phone.</summary>
    public const int MaxPerWindow = 3;

    public static readonly TimeSpan ThrottleWindow = TimeSpan.FromHours(24);

    /// <summary>A newer link was sent to the patient.</summary>
    public const string ReplacedReason = "replaced";

    /// <summary>The patient's registered number changed: the link went to a number that may no longer be theirs.</summary>
    public const string NumberChangedReason = "number_changed";

    /// <summary>The facility whose staff sent it, whose name and message settings it went with.</summary>
    public Guid FacilityId { get; set; }

    public Guid PatientId { get; set; }

    /// <summary>The number (E.164) it was sent to: the patient's registered number at the time.</summary>
    public string Phone { get; set; } = string.Empty;

    /// <summary>SHA-256 of the token in the link, as <see cref="SecretTokens.Hash"/> writes it; never the token.</summary>
    [SensitiveData]
    public string TokenHash { get; set; } = string.Empty;

    public PortalLinkChannels Channels { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>The first time a sign-in code was asked for through it.</summary>
    public DateTimeOffset? OpenedAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary><see cref="ReplacedReason"/> or <see cref="NumberChangedReason"/>.</summary>
    public string? RevokedReason { get; set; }

    public PortalLinkState StateAt(DateTimeOffset now) =>
        RevokedAt is not null ? PortalLinkState.Revoked
        : now >= ExpiresAt ? PortalLinkState.Expired
        : OpenedAt is not null ? PortalLinkState.Opened
        : PortalLinkState.Active;

    /// <summary>Not revoked and not expired: what the link itself decides, before the patient's record is looked at.</summary>
    public bool IsLive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// Whether the link can start a sign-in for <paramref name="patient"/> now: it is live, and the record is one a link
    /// may still be used for (<see cref="PortalLinkRules.MayHaveLinks"/>) and still has the number the link went to. A
    /// record changed after the link was sent (a new number, a protected category, a merge, a death) closes it at once.
    /// </summary>
    public bool Opens(Patient patient, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(patient);
        return IsLive(now) && patient.Id == PatientId && PortalLinkRules.MayHaveLinks(patient) && patient.Phone == Phone;
    }

    /// <summary>The first sign-in code asked for through the link opens it; later ones change nothing.</summary>
    public void MarkOpened(DateTimeOffset now) => OpenedAt ??= now;

    /// <summary>Stops the link; one already stopped keeps its first reason.</summary>
    public void Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
    }

    /// <summary>
    /// A token that could be one of HC360's (128 random bits in base64url, 22 characters, <see cref="SecretTokens.NewOpaqueToken"/>),
    /// checked before anything is looked up: anything else is not a link.
    /// </summary>
    public static bool IsWellFormedToken(string? token) =>
        token is { Length: >= 16 and <= 64 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>Who may be sent a portal link (PP-01), and the refusals staff see, each with a code of its own.</summary>
public static class PortalLinkRules
{
    public const string NoAddressCode = "portal.link_no_address";
    public const string InactiveCode = "portal.link_inactive";
    public const string NoMobileCode = "portal.link_no_mobile";
    public const string SensitiveCode = "portal.link_sensitive";
    public const string WhatsAppStoppedCode = "portal.link_whatsapp_stopped";
    public const string ThrottledCode = "portal.link_throttled";

    public static Error NoAddress { get; } = Error.Rule(NoAddressCode,
        "The patient portal is switched off, or its public address (portal.baseUrl) is not set, so there is no link to send. Ask your administrator.");

    public static Error Inactive { get; } = Error.Rule(InactiveCode,
        "This record is no longer in use (inactive, merged into another record, or the patient has died), so no portal link is sent for it.");

    public static Error NoMobile { get; } = Error.Rule(NoMobileCode,
        "The patient has no Indian mobile number on record. Add it to the registration first.");

    public static Error Sensitive { get; } = Error.Rule(SensitiveCode,
        "This record is in a protected category. Give portal access at the front desk after checking that the number is the patient's own.");

    public static Error WhatsAppStopped { get; } = Error.Rule(WhatsAppStoppedCode,
        "This number replied STOP on WhatsApp, so the link can't be sent there. Send it by SMS.");

    public static Error Throttled { get; } = Error.Rule(ThrottledCode,
        $"This patient has been sent {PortalLink.MaxPerWindow} portal links in the last 24 hours. Help them sign in at the desk, or send one tomorrow.");

    /// <summary>
    /// A record a link may be sent for, or used for: in use (not inactive, merged or deceased), and in no protected
    /// category. A link goes by message to whoever holds the phone; for an HIV, mental health, MTP or sexual-violence
    /// record, staff give portal access at the desk, once they have seen that the number is the patient's own.
    /// </summary>
    public static bool MayHaveLinks(Patient patient)
    {
        ArgumentNullException.ThrowIfNull(patient);
        return IsInUse(patient) && patient.SensitiveCategories == SensitiveCategory.None;
    }

    /// <summary>Why no link can be sent for the record, or null: in the order staff can act on (the record, the number, the category).</summary>
    public static Error? WhyNotSend(Patient patient)
    {
        ArgumentNullException.ThrowIfNull(patient);
        return !IsInUse(patient) ? Inactive
            : !SharedKernel.Phone.IsIndianMobile(patient.Phone) ? NoMobile
            : patient.SensitiveCategories != SensitiveCategory.None ? Sensitive
            : null;
    }

    /// <summary>Whether <paramref name="sentInWindow"/> links in the last <see cref="PortalLink.ThrottleWindow"/> leave room for another.</summary>
    public static bool IsThrottled(int sentInWindow) => sentInWindow >= PortalLink.MaxPerWindow;

    /// <summary>The start of the window the links sent are counted in, for a link sent at <paramref name="now"/>.</summary>
    public static DateTimeOffset WindowStart(DateTimeOffset now) => now - PortalLink.ThrottleWindow;

    private static bool IsInUse(Patient patient) => patient.IsActive && patient.MergedIntoPatientId is null && !patient.IsDeceased;
}
