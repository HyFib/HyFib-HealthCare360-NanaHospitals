using System.Globalization;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Integrations.Features.LabConnections;

/// <summary>
/// System health's warnings about the network analysers (ASVS V13.2.1): an analyser connects without signing in, so its
/// fixed IP address is the only thing that tells HC360 the results are its own. Lab setup requires the address; a
/// connection saved without one before it did takes results from any device that reaches its port, and is warned of until
/// the address is entered (<c>lab.analyser_any_address</c>). A device refused on an analyser's port in the last day is
/// warned of with its address (<c>lab.analyser_address_refused</c>): a re-addressed analyser, or something on the lab
/// network pretending to be one. Serial analysers, relayed by an enrolled lab agent, and the training analyser have no
/// address and are not warned of.
/// </summary>
internal sealed class LabConnectionHealthWarnings(IntegrationSettingsReader settings, LabStatusBoard status, TimeProvider time) : IHealthWarningSource
{
    /// <summary>How long a refused address stays on System health after the last refusal.</summary>
    internal static readonly TimeSpan RefusedShownFor = TimeSpan.FromDays(1);

    /// <summary>A network connection with no IP address pinned: its listener takes any device.</summary>
    internal static bool OpenToAnyDevice(LabInstrumentSettings instrument) =>
        instrument.Transport == LabTransport.Tcp && LabInstrumentListener.ParseAllowList(instrument.AllowedIps) is null;

    public async Task<IReadOnlyList<HealthWarning>> WarningsAsync(CancellationToken cancellationToken)
    {
        var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, cancellationToken);
        if (gateway?.Enabled != true)
        {
            return [];
        }

        var instruments = await settings.ReadValueAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, null, cancellationToken) ?? [];
        var live = await status.ReadAsync(cancellationToken);
        return Warnings(instruments, live.Statuses, time.GetUtcNow());
    }

    /// <summary>The warnings for these connections and their status (pure; unit-tested).</summary>
    internal static IReadOnlyList<HealthWarning> Warnings(
        IReadOnlyCollection<LabInstrumentSettings> instruments, IReadOnlyCollection<LabListenerStatus> statuses, DateTimeOffset now)
    {
        var warnings = new List<HealthWarning>();
        var open = instruments.Where(i => i.Enabled && OpenToAnyDevice(i)).Select(i => i.Code.ToUpperInvariant()).Order(StringComparer.Ordinal).ToList();
        if (open.Count > 0)
        {
            warnings.Add(new HealthWarning("lab.analyser_any_address", "warning",
                $"Analyser {string.Join(", ", open)} takes results from any device on the network: enter the analyser's fixed IP address in its connection (Lab setup › Analysers)."));
        }

        var network = instruments.Where(i => i.Transport == LabTransport.Tcp).Select(i => i.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var said in statuses.Where(s => network.Contains(s.InstrumentCode)).OrderBy(s => s.InstrumentCode, StringComparer.OrdinalIgnoreCase))
        {
            var recent = said.RefusedAddresses.Where(r => now - r.LastAt <= RefusedShownFor).ToList();
            if (recent.Count == 0)
            {
                continue;
            }

            var which = string.Join(", ", recent.Select(r =>
                $"{r.Address} ({r.Count.ToString(CultureInfo.InvariantCulture)} time{(r.Count == 1 ? string.Empty : "s")}, last {DocumentFormat.DateTime(r.LastAt)})"));
            warnings.Add(new HealthWarning("lab.analyser_address_refused", "warning",
                $"Analyser {said.InstrumentCode.ToUpperInvariant()} refused connections from an address that is not its IP: {which}. Nothing they sent was read. If the analyser's address changed, enter the new one in Lab setup › Analysers; otherwise find the device."));
        }

        return warnings;
    }
}
