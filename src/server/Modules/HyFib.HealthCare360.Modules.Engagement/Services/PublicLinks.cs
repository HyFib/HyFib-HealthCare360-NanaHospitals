using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Links sent to patients (feedback forms, opt-out, follow-up reminders). The base is the hospital's public https
/// address from the <c>portal.baseUrl</c> setting, read and validated by <see cref="PortalAddress"/> (shared with
/// Scheduling, Telehealth and the printed QR codes), and every link is a page of the patient portal
/// (<c>{base}/portal/...</c>). A missing or unusable setting reads as "not configured" instead of failing the caller —
/// discharge, for example, must never roll back because a feedback link could not be built.
/// </summary>
public sealed class PublicLinks(ISettingsService settings)
{
    public const string BaseUrlSetting = PortalAddress.BaseUrlSetting;

    public Task<string?> BaseUrlAsync(Guid? facilityId, CancellationToken ct) => PortalAddress.BaseUrlAsync(settings, facilityId, ct);

    /// <summary>Same rules as <see cref="PortalAddress.Normalize"/>.</summary>
    public static string? Normalize(string? configured) => PortalAddress.Normalize(configured);

    /// <summary>Personal single-use feedback link (SMS after discharge or a visit).</summary>
    public static string Feedback(string baseUrl, string token) => PortalAddress.Page(baseUrl, $"feedback/{token}");

    /// <summary>Anonymous form link printed as a QR code (poster or kiosk); the token identifies the form only.</summary>
    public static string FeedbackForm(string baseUrl, string token) => PortalAddress.Page(baseUrl, $"feedback/form/{token}");

    public static string OptOut(string baseUrl, string token) => PortalAddress.Page(baseUrl, $"unsubscribe/{token}");

    /// <summary>The portal's home page (sign in, book the follow-up).</summary>
    public static string PortalHome(string baseUrl) => PortalAddress.Page(baseUrl, string.Empty);
}

/// <summary>Hospital-local (IST) date helpers for the Engagement module.</summary>
public static class EngagementClock
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    /// <summary>UTC instant at which the local <paramref name="date"/> starts (IST has no daylight saving).</summary>
    public static DateTimeOffset StartOfDayUtc(DateOnly date) => new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();

    public static DateTime ToLocal(DateTimeOffset instant) => instant.ToOffset(Ist).DateTime;

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(ToLocal(instant));

    public static string LocalMonth(DateTimeOffset instant) => ToLocal(instant).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
