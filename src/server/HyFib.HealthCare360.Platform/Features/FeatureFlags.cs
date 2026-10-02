using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Features;

/// <summary>Reads the feature switches (<see cref="FeatureCatalog"/>).</summary>
public interface IFeatureFlags
{
    /// <summary>
    /// Whether the feature is on for the facility. The hospital-wide switch (else the catalogue default) is the master:
    /// while it is off the feature is off everywhere. While it is on, the facility's own <c>false</c> turns the feature
    /// off for that facility; a facility's <c>true</c> changes nothing. <paramref name="facilityId"/> null reads the
    /// hospital-wide switch alone, which is therefore right for callers without a facility (patients, guests): no branch
    /// can be on while it is off.
    /// </summary>
    Task<bool> IsEnabledAsync(string feature, Guid? facilityId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature switches over <see cref="ISettingsService"/> (cached for a minute; a change made on this server applies at
/// once). Only the JSON values <c>true</c> and <c>false</c> count: anything else, such as <c>"no"</c> or <c>null</c>, is
/// ignored, so a value that is not a switch leaves the feature as the hospital-wide switch has it and a typo never turns
/// a feature off by accident.
/// </summary>
internal sealed class FeatureFlags(ISettingsService settings) : IFeatureFlags
{
    public async Task<bool> IsEnabledAsync(string feature, Guid? facilityId, CancellationToken cancellationToken = default)
    {
        var definition = FeatureCatalog.Get(feature);
        if (!(Switch(await settings.GetAsync<JsonElement?>(definition.Key, null, cancellationToken)) ?? definition.DefaultEnabled))
        {
            return false;
        }

        return facilityId is null || Switch(await settings.GetAsync<JsonElement?>(definition.Key, facilityId, cancellationToken)) != false;
    }

    /// <summary>The switch a stored value sets: on or off for JSON <c>true</c>/<c>false</c>, null for anything else.</summary>
    internal static bool? Switch(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
