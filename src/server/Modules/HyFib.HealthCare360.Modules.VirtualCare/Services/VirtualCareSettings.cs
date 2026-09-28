using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Administrator settings for virtual care: ICE servers (TEL-06), how early patients may enter the waiting room, and
/// the RPM device-silence threshold (RPM-05). Values are read leniently and clamped so a typo never breaks calls.
/// </summary>
public sealed class VirtualCareSettings(ISettingsService settings)
{
    /// <summary>JSON array of RTCIceServer objects, e.g. [{"urls":["turn:turn.example.in:3478"],"username":"u","credential":"p"}].</summary>
    public const string IceServersKey = "telehealth.iceServers";

    public const string EarlyJoinKey = "telehealth.earlyJoinMinutes";
    public const string DeviceSilenceHoursKey = "rpm.deviceSilenceHours";
    public const int DefaultDeviceSilenceHours = 24;

    public async Task<IReadOnlyList<IceServer>> IceServersAsync(Guid? facilityId, CancellationToken ct) =>
        IceServers.Parse(await ReadAsync(IceServersKey, facilityId, ct));

    public async Task<TimeSpan> EarlyJoinAsync(Guid? facilityId, CancellationToken ct) =>
        TimeSpan.FromMinutes(Math.Clamp(await ReadIntAsync(EarlyJoinKey, facilityId, TelehealthSession.DefaultEarlyJoinMinutes, ct), 0, 240));

    public async Task<TimeSpan> DeviceSilenceAsync(Guid? facilityId, CancellationToken ct) =>
        TimeSpan.FromHours(Math.Clamp(await ReadIntAsync(DeviceSilenceHoursKey, facilityId, DefaultDeviceSilenceHours, ct), 1, 720));

    private async Task<int> ReadIntAsync(string key, Guid? facilityId, int fallback, CancellationToken ct)
    {
        var element = await ReadAsync(key, facilityId, ct);
        if (element is not { } value)
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private async Task<JsonElement?> ReadAsync(string key, Guid? facilityId, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<JsonElement?>(key, facilityId, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
