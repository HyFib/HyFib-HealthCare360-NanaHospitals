using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Links sent to patients (feedback forms, opt-out). The base is the hospital's published portal address from the
/// <c>portal.baseUrl</c> setting (shared with Scheduling and Telehealth). A bare host name is read as https, and only
/// an absolute https URL without credentials is accepted, so a misconfiguration can never put a plain-text link (with
/// its personal token) into an SMS. A malformed setting reads as "not configured" instead of failing the caller —
/// discharge, for example, must never roll back because a feedback link could not be built.
/// </summary>
public sealed class PublicLinks(ISettingsService settings)
{
    public const string BaseUrlSetting = "portal.baseUrl";

    public async Task<string?> BaseUrlAsync(Guid? facilityId, CancellationToken ct)
    {
        try
        {
            var value = await settings.GetAsync<JsonElement?>(BaseUrlSetting, facilityId, ct);
            return value is { ValueKind: JsonValueKind.String } text ? Normalize(text.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Absolute https base URL without a trailing slash, query or fragment; null when unusable.</summary>
    public static string? Normalize(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var candidate = configured.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
    }

    /// <summary>Personal single-use feedback link (SMS after discharge or a visit).</summary>
    public static string Feedback(string baseUrl, string token) => $"{baseUrl}/feedback/{token}";

    /// <summary>Anonymous form link printed as a QR code (poster or kiosk); the token identifies the form only.</summary>
    public static string FeedbackForm(string baseUrl, string token) => $"{baseUrl}/feedback/form/{token}";

    public static string OptOut(string baseUrl, string token) => $"{baseUrl}/unsubscribe/{token}";
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
