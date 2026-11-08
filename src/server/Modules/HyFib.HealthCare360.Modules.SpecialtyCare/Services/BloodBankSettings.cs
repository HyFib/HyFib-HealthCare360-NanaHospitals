using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>The blood bank's facility settings.</summary>
public static class BloodBankSettings
{
    /// <summary>
    /// Whether a second blood bank officer verifies a donation's screening results before its units leave quarantine, and
    /// issues a unit one officer both grouped and cross-matched (on unless the facility sets it to <c>false</c>: the Drugs
    /// and Cosmetics Rules, Schedule F Part XII-B, and the NBTC standards expect the medical officer's check). A blood bank
    /// with one officer on duty switches it off: that officer then verifies and issues alone, and what they release so is
    /// listed for the medical officer's review. Switching it off waits for a second administrator (SettingGuard).
    /// </summary>
    public const string SeparateVerifier = "bloodbank.separateVerifier";

    /// <summary>Whether <paramref name="facilityId"/> asks for a second officer (<see cref="SeparateVerifier"/>).</summary>
    public static async Task<bool> SeparateVerifierAsync(this ISettingsService settings, Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<bool?>(SeparateVerifier, facilityId, ct) != false;
}
