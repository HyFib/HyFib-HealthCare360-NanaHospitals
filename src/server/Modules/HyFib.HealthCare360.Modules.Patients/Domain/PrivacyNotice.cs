using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>
/// The version of the privacy notice the hospital gives patients (DPDP Act 2023 s.5; DPDP Rules 2025 rule 3): the
/// <see cref="SettingKey"/> setting, which the privacy officer raises whenever the notice's text changes, "1" until then.
/// A consent captured without a version of its own records this one, so each consent says which notice the patient had.
/// </summary>
public static class PrivacyNotice
{
    public const string SettingKey = "privacy.noticeVersion";

    public const string DefaultVersion = "1";

    public static async Task<string> CurrentVersionAsync(ISettingsService settings, Guid? facilityId, CancellationToken ct)
    {
        var version = (await settings.GetAsync<string?>(SettingKey, facilityId, ct))?.Trim();
        return string.IsNullOrEmpty(version) ? DefaultVersion : version[..Math.Min(version.Length, 16)];
    }
}
