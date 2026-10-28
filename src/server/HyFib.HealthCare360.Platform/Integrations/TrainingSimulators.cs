using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.Platform.Integrations;

/// <summary>
/// Every integration has a simulator, so staff can practise with nothing sent (SRS §5): it answers "sent", "paid",
/// "approved" or "authenticated" with no message sent, no money moving and nobody checked, and keeps what it was given,
/// sign-in codes too, in an inbox administrators read. Only a training or test server may use them
/// (<see cref="PlatformOptions.AllowsSimulators"/>). On a hospital's live server an integration cannot be set to its
/// simulator, and one set to it before is not usable: the integration reads its settings as not usable, with the reason.
/// </summary>
public static class TrainingSimulators
{
    /// <summary>The provider key of every simulator (an integration's <c>provider</c> setting).</summary>
    private const string Provider = "simulator";

    /// <summary>
    /// Why <paramref name="value"/> cannot be saved as <paramref name="key"/> on this server, or null: on a live server
    /// an integration's settings (<c>integrations.&lt;name&gt;</c>) cannot name the simulator as their provider.
    /// </summary>
    public static string? Problem(string key, JsonNode? value, PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(options);
        return !options.AllowsSimulators && key.StartsWith("integrations.", StringComparison.Ordinal) && NamesSimulator(value)
            ? "Only a training or test server, one set up with demonstration data, can use the simulator. Choose the real provider."
            : null;
    }

    /// <summary>Whether <paramref name="provider"/> is the simulator, as integrations read it (any case, spaces trimmed).</summary>
    public static bool IsSimulator(string? provider) => string.Equals(provider?.Trim(), Provider, StringComparison.OrdinalIgnoreCase);

    // Settings are read with web defaults, which match property names in any case.
    private static bool NamesSimulator(JsonNode? value) =>
        value is JsonObject settings && settings.Any(property =>
            string.Equals(property.Key, "provider", StringComparison.OrdinalIgnoreCase)
            && property.Value is JsonValue provider && provider.TryGetValue<string>(out var name) && IsSimulator(name));
}
