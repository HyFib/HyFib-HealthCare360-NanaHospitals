using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Administrator-tunable scheduling settings (Administration → Settings). Values are read leniently (a number or a
/// numeric string) and clamped, so a typing mistake in the settings screen can never stop online booking.
/// </summary>
public sealed class SchedulingSettings(ISettingsService settings)
{
    /// <summary>Minutes before a slot when online booking and online changes close (default 30).</summary>
    public const string OnlineCutoffKey = "scheduling.onlineBookingCutoffMinutes";

    /// <summary>How many days ahead patients can book online (default 30).</summary>
    public const string OnlineWindowKey = "scheduling.onlineBookingWindowDays";

    /// <summary>Public address of the patient portal, used in SMS/WhatsApp links (shared with Telehealth).</summary>
    public const string PortalBaseUrlKey = "portal.baseUrl";

    public const int DefaultCutoffMinutes = 30;
    public const int DefaultWindowDays = 30;

    public async Task<OnlineBookingPolicy> OnlinePolicyAsync(Guid? facilityId, CancellationToken ct)
    {
        var cutoff = await ReadIntAsync(OnlineCutoffKey, facilityId, DefaultCutoffMinutes, ct);
        var window = await ReadIntAsync(OnlineWindowKey, facilityId, DefaultWindowDays, ct);
        return new OnlineBookingPolicy(TimeSpan.FromMinutes(Math.Clamp(cutoff, 0, 7 * 24 * 60)), Math.Clamp(window, 1, 180));
    }

    /// <summary>Absolute https URL of the portal without a trailing slash, or null when not configured.</summary>
    public async Task<string?> PortalBaseUrlAsync(Guid? facilityId, CancellationToken ct)
    {
        var value = await ReadStringAsync(PortalBaseUrlKey, facilityId, ct);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim().TrimEnd('/');
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
    }

    private async Task<int> ReadIntAsync(string key, Guid? facilityId, int fallback, CancellationToken ct)
    {
        var element = await ReadAsync(key, facilityId, ct);
        return element switch
        {
            { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var number) => number,
            { ValueKind: JsonValueKind.String } s when int.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    private async Task<string?> ReadStringAsync(string key, Guid? facilityId, CancellationToken ct)
    {
        var element = await ReadAsync(key, facilityId, ct);
        return element is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    private async Task<JsonElement?> ReadAsync(string key, Guid? facilityId, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<JsonElement?>(key, facilityId, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
