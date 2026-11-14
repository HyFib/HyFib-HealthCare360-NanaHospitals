using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The one field of the facility's POS integration setting (<c>integrations.pos</c>) that Billing needs: how long the
/// terminal keeps an unpaid sale open before it cancels it. Billing cannot see the integration's own settings type, so it
/// reads the same row for this field alone; it is the window in which a sale whose upload answer was lost may still be on
/// the terminal (<see cref="PosUnconfirmed"/>).
/// </summary>
internal sealed record PosAutoCancelSettings
{
    public const string Key = "integrations.pos";

    /// <summary>The terminal cancels an unpaid transaction after this many minutes (the integration's default is 5).</summary>
    public int AutoCancelMinutes { get; init; } = 5;

    public static async Task<TimeSpan> WindowAsync(ISettingsService settings, Guid facilityId, CancellationToken ct)
    {
        try
        {
            var read = await settings.GetAsync<PosAutoCancelSettings>(Key, facilityId, ct);
            return PosUnconfirmed.Window((read ?? new PosAutoCancelSettings()).AutoCancelMinutes);
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed setting must not stop a sale or the re-check: the default window applies until it is corrected.
            return PosUnconfirmed.Window(new PosAutoCancelSettings().AutoCancelMinutes);
        }
    }
}
