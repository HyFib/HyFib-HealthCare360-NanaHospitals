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

    /// <summary>
    /// The two-sample rule (on unless the facility sets it to <c>false</c>): a patient with no blood group on record at the
    /// blood bank has the group of the first sample confirmed on a second, separately drawn sample before group-specific
    /// red cells or plasma are cross-matched or issued, so that a wrong-blood-in-tube sample is found before blood is given
    /// on it (BSH guidelines for pre-transfusion compatibility procedures). The rule in force when a request's first sample
    /// is grouped applies to that request. A transfusion committee that documents another policy switches it off, which
    /// waits for a second administrator (SettingGuard).
    /// </summary>
    public const string TwoSampleRule = "bloodbank.twoSampleRule";

    /// <summary>Whether <paramref name="facilityId"/> asks for a second officer (<see cref="SeparateVerifier"/>).</summary>
    public static async Task<bool> SeparateVerifierAsync(this ISettingsService settings, Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<bool?>(SeparateVerifier, facilityId, ct) != false;

    /// <summary>Whether <paramref name="facilityId"/> applies the two-sample rule (<see cref="TwoSampleRule"/>).</summary>
    public static async Task<bool> TwoSampleRuleAsync(this ISettingsService settings, Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<bool?>(TwoSampleRule, facilityId, ct) != false;
}
