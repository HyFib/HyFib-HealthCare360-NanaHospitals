using System.Text.Json;

namespace HyFib.HealthCare360.Platform.Settings;

/// <summary>
/// The hospital's public address for every link a patient receives or scans: SMS/WhatsApp links (feedback, opt-out,
/// waitlist offers, telehealth) and the QR codes printed on prescriptions and reports. The <c>portal.baseUrl</c>
/// setting (per facility, else global) is the server's public https <b>origin</b>, for example
/// <c>https://care.nanahospitals.in</c>; the patient portal is served under <c>/portal</c>, so patient links are
/// <c>{base}/portal/...</c>.
/// <list type="bullet">
/// <item>A bare host name is read as https, and only an absolute https URL without credentials is accepted, so a
/// misconfiguration can never put a plain-text link (with its personal token) into an SMS.</item>
/// <item>Query, fragment and trailing slash are dropped, and so is a trailing <c>/portal</c>, so either spelling an
/// administrator types works.</item>
/// <item>A malformed setting reads as "not configured" instead of failing the caller: a discharge, for example, must
/// never roll back because a feedback link could not be built.</item>
/// </list>
/// </summary>
public static class PortalAddress
{
    public const string BaseUrlSetting = "portal.baseUrl";

    /// <summary>Path under which the server hosts the patient portal app.</summary>
    public const string PortalPath = "/portal";

    /// <summary>The normalised public https address for the facility, or null when it is not configured or unusable.</summary>
    public static async Task<string?> BaseUrlAsync(ISettingsService settings, Guid? facilityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var value = await settings.GetAsync<JsonElement?>(BaseUrlSetting, facilityId, cancellationToken);
            return value is { ValueKind: JsonValueKind.String } text ? Normalize(text.GetString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Absolute https base without credentials, query, fragment, trailing slash or trailing <c>/portal</c>; null when unusable.</summary>
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

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0
            || uri.Host.Length == 0)
        {
            return null;
        }

        var address = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return address.EndsWith(PortalPath, StringComparison.OrdinalIgnoreCase) ? address[..^PortalPath.Length] : address;
    }

    /// <summary>
    /// A page of the patient portal: <c>{baseUrl}/portal/{path}</c>. Escape token or code segments before passing
    /// them. With an empty <paramref name="baseUrl"/> the result is the server-relative path (<c>/portal/{path}</c>).
    /// </summary>
    public static string Page(string baseUrl, string path)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(path);
        return $"{baseUrl}{PortalPath}/{path.TrimStart('/')}";
    }
}
