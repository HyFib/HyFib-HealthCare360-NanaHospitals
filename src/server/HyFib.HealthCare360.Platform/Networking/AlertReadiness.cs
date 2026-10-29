using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// What decides whether a critical warning reaches IT on-call by phone (audit P2): the on-call number, the SMS and WhatsApp
/// providers, and the hospital-wide <c>system.alert</c> SMS template that carries the warning.
/// </summary>
/// <param name="OnCallPhone">The IT on-call number (<c>alerts.oncallPhone</c>), if one is set.</param>
/// <param name="SmsProvider">The hospital's SMS provider (<c>integrations.sms</c>) while SMS is switched on; null when it is off.</param>
/// <param name="AlertSmsTemplate">An active hospital-wide <c>system.alert</c> SMS template exists.</param>
/// <param name="AlertDltTemplateId">That template's DLT template id, if it has one.</param>
/// <param name="WhatsAppToOnCall">The alerts go to the on-call number on WhatsApp too, and WhatsApp is switched on with a real provider.</param>
public sealed record AlertRoute(string? OnCallPhone, string? SmsProvider, bool AlertSmsTemplate, string? AlertDltTemplateId, bool WhatsAppToOnCall);

/// <summary>
/// System health's warning, on a live server, when a critical warning would not reach IT on-call by phone (audit P2). The
/// alerts themselves (<see cref="SystemHealthAlerts"/>) cannot say so: they are what fails. Critical when SMS is set up
/// yet every alert SMS fails, unseen: there is no <c>system.alert</c> SMS template, or MSG91 refuses it for want of a DLT
/// template id, and nothing goes on WhatsApp either. A warning when nobody set them up, or when the operators may drop an
/// SMS whose template has no DLT id. WhatsApp alone, by the hospital's choice, is no warning.
/// </summary>
public static class AlertReadiness
{
    public const string Code = "alerts.not_ready";

    /// <summary>The SMS template that carries System health's critical warnings to IT on-call.</summary>
    public const string AlertTemplate = "system.alert";

    private const string Simulator = "simulator";

    /// <summary>The warning on a live server, read from the settings and the message templates; null when the alerts can reach IT.</summary>
    public static async Task<HealthWarning?> WarningAsync(HospitalDbContext db, ISettingsService settings, PlatformOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowsSimulators)
        {
            return null;
        }

        var onCall = await WifiMonitor.OnCallAsync(settings, ct);
        var template = await db.Set<NotificationTemplate>().AsNoTracking()
            .Where(t => t.Key == AlertTemplate && t.Channel == NotificationChannel.Sms && t.FacilityId == null && t.IsActive)
            .OrderBy(t => t.Language == "en" ? 0 : 1)
            .Select(t => new { t.DltTemplateId })
            .FirstOrDefaultAsync(ct);
        var whatsApp = await ProviderAsync(settings, "integrations.whatsapp", ct);
        return Warning(new AlertRoute(
            onCall.Phone,
            await ProviderAsync(settings, "integrations.sms", ct),
            template is not null,
            string.IsNullOrWhiteSpace(template?.DltTemplateId) ? null : template.DltTemplateId,
            onCall.WhatsApp && whatsApp is not null and not Simulator));
    }

    /// <summary>The warning for <paramref name="route"/>, or null when a critical warning reaches IT on-call.</summary>
    public static HealthWarning? Warning(AlertRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (string.IsNullOrWhiteSpace(route.OnCallPhone))
        {
            return new(Code, "warning",
                "No IT on-call number is set (alerts.oncallPhone under Administration › Settings), so critical warnings reach only those who open HC360. Set IT's mobile number.");
        }

        var sms = route.SmsProvider is { Length: > 0 } provider && provider != Simulator ? provider : null;
        if (sms is null)
        {
            // WhatsApp alone is the hospital's choice; nothing at all is a gap nobody filled.
            return route.WhatsAppToOnCall
                ? null
                : new(Code, "warning",
                    "Critical warnings do not reach IT on-call by phone: SMS is not set up (Administration › Integrations), and they do not go on WhatsApp (alerts.oncallWhatsApp).");
        }

        string? failure = null;
        if (!route.AlertSmsTemplate)
        {
            failure = "there is no active system.alert SMS template, so every alert SMS fails (Administration › Message templates)";
        }
        else if (route.AlertDltTemplateId is null && sms == "msg91")
        {
            failure = "the system.alert SMS template has no DLT template id, which MSG91 requires, so every alert SMS fails. "
                + "Enter the id its text was registered with on the DLT portal (Administration › Message templates)";
        }
        else if (route.AlertDltTemplateId is null && sms != "twilio")
        {
            return new(Code, "warning",
                "Critical warnings to IT on-call may not arrive by SMS: the system.alert SMS template has no DLT template id, and Indian operators drop SMS "
                + "whose template is not registered on DLT. Register its text on the DLT portal and enter the id (Administration › Message templates).");
        }

        if (failure is null)
        {
            return null;
        }

        // Set up, yet failing every time unseen: critical, unless WhatsApp still carries the warnings.
        return route.WhatsAppToOnCall
            ? new(Code, "warning", $"Critical warnings reach IT on-call on WhatsApp only: {failure}.")
            : new(Code, "critical", $"Critical warnings do not reach IT on-call by phone: {failure}.");
    }

    /// <summary>An integration's provider while it is switched on (<c>enabled</c>), else null; the settings are free JSON.</summary>
    private static async Task<string?> ProviderAsync(ISettingsService settings, string key, CancellationToken ct)
    {
        if (await settings.GetAsync<JsonElement?>(key, cancellationToken: ct) is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        var enabled = Property(value, "enabled") is { ValueKind: JsonValueKind.True };
        return enabled && Property(value, "provider") is { ValueKind: JsonValueKind.String } provider
            ? provider.GetString()?.Trim().ToLowerInvariant()
            : null;
    }

    private static JsonElement? Property(JsonElement value, string name)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
