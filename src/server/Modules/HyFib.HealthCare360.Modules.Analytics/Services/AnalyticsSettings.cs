using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>Runtime settings of the analytics module (platform settings API, global or per facility).</summary>
internal static class AnalyticsSettings
{
    /// <summary>
    /// JSON array of e-mail domains scheduled reports may be sent to, e.g. <c>["nanahospitals.in"]</c>. Empty or absent
    /// means any address (set it before go-live so reports cannot be e-mailed to personal mailboxes).
    /// </summary>
    public const string AllowedEmailDomains = "analytics.schedules.allowedEmailDomains";

    /// <summary>Days a delivered file is kept for download before its content is purged (default 30).</summary>
    public const string DeliveryRetentionDays = "analytics.deliveries.retentionDays";

    public const int DefaultRetentionDays = 30;

    /// <summary>Largest file attached to an e-mail; bigger files are downloaded from HC360 instead.</summary>
    public const long MaxAttachmentBytes = 10 * 1024 * 1024;
}

/// <summary>Schedule inputs stored as a JSON object of canonical key/value pairs.</summary>
internal static class ScheduleParameters
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Write(IReadOnlyDictionary<string, string> parameters) => JsonSerializer.Serialize(parameters, Options);

    public static Dictionary<string, string?> Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options) ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }
}
