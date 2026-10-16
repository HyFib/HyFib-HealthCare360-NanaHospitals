namespace HyFib.HealthCare360.Integrations.Biometric;

/// <summary>
/// The platform's <see cref="IAttendanceTerminalClient"/>: reads a terminal with the provider its facility uses
/// (<c>integrations.biometric</c>). Until that is set up, reading a terminal fails with the reason, and terminals that
/// push to HC360 are not affected.
/// </summary>
internal sealed class AttendanceTerminalRouter(IEnumerable<IAttendanceTerminalProvider> providers, IntegrationSettingsReader settings)
    : IAttendanceTerminalClient
{
    public async Task<AttendanceTerminalReading> ReadingAsync(Guid facilityId, CancellationToken cancellationToken)
    {
        var (provider, _, error) = await ResolveAsync(facilityId, cancellationToken);
        return new AttendanceTerminalReading(provider?.Key, error);
    }

    public async Task<AttendanceTerminalRead> ReadAsync(AttendanceTerminal terminal, CancellationToken cancellationToken)
    {
        // Terminals are read by a job with no signed-in user: the log rows count against the terminal's facility.
        using var facility = IntegrationTrace.ForFacility(terminal.FacilityId);
        var (provider, config, error) = await ResolveAsync(terminal.FacilityId, cancellationToken);
        return provider is null
            ? AttendanceTerminalRead.Failed(error!)
            : await provider.ReadAsync(terminal, config!.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<string?> SetClockAsync(AttendanceTerminal terminal, DateTime localTime, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(terminal.FacilityId);
        var (provider, config, error) = await ResolveAsync(terminal.FacilityId, cancellationToken);
        return provider is null ? error : await provider.SetClockAsync(terminal, localTime, config!.Settings!, config.Secrets, cancellationToken);
    }

    public async Task<string?> PunchOnTrainingTerminalAsync(AttendanceTerminal terminal, string pin, DateTime localTime, CancellationToken cancellationToken)
    {
        using var facility = IntegrationTrace.ForFacility(terminal.FacilityId);
        var (provider, _, error) = await ResolveAsync(terminal.FacilityId, cancellationToken);
        if (provider is null)
        {
            return error;
        }

        if (provider is not SimulatorAttendanceTerminal simulator)
        {
            return "Punches can be made here only while Biometric attendance uses the simulator, for training. Punch on the terminal itself.";
        }

        await simulator.PunchAsync(terminal, pin, localTime, cancellationToken);
        return null;
    }

    private async Task<(IAttendanceTerminalProvider? Provider, IntegrationConfig<BiometricSettings, BiometricSecrets>? Config, string? Error)> ResolveAsync(
        Guid facilityId, CancellationToken cancellationToken)
    {
        var config = await settings.ReadAsync<BiometricSettings, BiometricSecrets>(
            IntegrationNames.Biometric, facilityId, requireEnabled: true, cancellationToken);
        if (config.ActiveProvider is not { } key)
        {
            return (null, null, config.Problem ?? "Reading terminals is not available.");
        }

        var provider = providers.FirstOrDefault(p => p.Key == key);
        return provider is null ? (null, null, $"Unknown biometric provider '{key}'. Use zkteco or simulator.") : (provider, config, null);
    }
}
