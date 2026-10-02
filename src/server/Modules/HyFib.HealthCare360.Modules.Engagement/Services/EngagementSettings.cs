using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Administrator settings for automatic follow-ups (CRM-02), per facility or global. Values are read leniently and
/// clamped, so a typo falls back to a sensible range instead of breaking the reminders.
/// </summary>
public sealed class EngagementSettings(ISettingsService settings)
{
    public const string ReminderDaysKey = "crm.followUpReminderDays";
    public const string ChronicReviewMonthsKey = "crm.chronicReviewMonths";
    public const string HealthCheckupRecallMonthsKey = "crm.healthCheckupRecallMonths";

    /// <summary>The longest reminder lead the setting allows, which bounds the reminder job's search.</summary>
    public const int MaxReminderDays = 14;

    public async Task<int> ReminderDaysAsync(Guid? facilityId, CancellationToken ct) =>
        Math.Clamp(await ReadIntAsync(ReminderDaysKey, facilityId, FollowUpAutomation.DefaultReminderDays, ct), 0, MaxReminderDays);

    public async Task<int> ChronicReviewMonthsAsync(Guid? facilityId, CancellationToken ct) =>
        Math.Clamp(await ReadIntAsync(ChronicReviewMonthsKey, facilityId, FollowUpAutomation.DefaultChronicReviewMonths, ct), 1, 12);

    public async Task<int> HealthCheckupRecallMonthsAsync(Guid? facilityId, CancellationToken ct) =>
        Math.Clamp(await ReadIntAsync(HealthCheckupRecallMonthsKey, facilityId, FollowUpAutomation.DefaultHealthCheckupRecallMonths, ct), 3, 36);

    private async Task<int> ReadIntAsync(string key, Guid? facilityId, int fallback, CancellationToken ct)
    {
        JsonElement? element;
        try
        {
            element = await settings.GetAsync<JsonElement?>(key, facilityId, ct);
        }
        catch (JsonException)
        {
            return fallback;
        }

        return element switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var value) => value,
            { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }
}
