using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Platform.Features;

/// <summary>Reads the feature switches (<see cref="FeatureCatalog"/>).</summary>
public interface IFeatureFlags
{
    /// <summary>
    /// Whether the feature is on for the facility: the facility's own <c>true</c>/<c>false</c>, else the hospital-wide one,
    /// else the catalogue default. <paramref name="facilityId"/> null reads the hospital-wide value.
    /// </summary>
    Task<bool> IsEnabledAsync(string feature, Guid? facilityId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Feature switches over <see cref="ISettingsService"/> (cached for a minute; a change made on this server applies at
/// once). Only the JSON values <c>true</c> and <c>false</c> count: anything else, such as <c>"no"</c> or <c>null</c>, is
/// ignored, so a facility value that is not a switch falls through to the hospital-wide one and a typo never turns a
/// feature off by accident.
/// </summary>
internal sealed class FeatureFlags(ISettingsService settings) : IFeatureFlags
{
    public async Task<bool> IsEnabledAsync(string feature, Guid? facilityId, CancellationToken cancellationToken = default)
    {
        var definition = FeatureCatalog.Get(feature);
        if (facilityId is not null && Switch(await settings.GetAsync<JsonElement?>(definition.Key, facilityId, cancellationToken)) is { } forFacility)
        {
            return forFacility;
        }

        return Switch(await settings.GetAsync<JsonElement?>(definition.Key, null, cancellationToken)) ?? definition.DefaultEnabled;
    }

    /// <summary>The switch a stored value sets: on or off for JSON <c>true</c>/<c>false</c>, null for anything else.</summary>
    internal static bool? Switch(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
