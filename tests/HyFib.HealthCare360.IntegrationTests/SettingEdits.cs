using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Saves settings and feature switches as the administration screens do (REL-04): with the version of the value saved
/// now, or none when there is none yet. Tests set values up and put them back over their own saves.
/// </summary>
internal static class SettingEdits
{
    public static async Task SaveSettingAsync(this ApiClient admin, string key, SaveSettingRequest request) =>
        await admin.PutAsync($"/api/v1/admin/settings/{key}", request with { Version = await SettingVersionAsync(admin, key, request.FacilityId) });

    /// <summary>The version of the value saved for exactly this key and facility (null: hospital-wide), if one is.</summary>
    public static async Task<uint?> SettingVersionAsync(this ApiClient admin, string key, Guid? facilityId = null) =>
        (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={Uri.EscapeDataString(key)}"))
        .SingleOrDefault(s => s.Key == key && s.FacilityId == facilityId)?.Version;

    public static async Task<FeatureDto> SetFeatureAsync(this ApiClient admin, string feature, SetFeatureRequest request)
    {
        var current = (await admin.GetAsync<List<FeatureDto>>("/api/v1/admin/features")).Single(f => f.Key == feature || f.Code == feature);
        return await admin.PutAsync<FeatureDto>($"/api/v1/admin/features/{feature}", request with { Version = current.Version });
    }
}
