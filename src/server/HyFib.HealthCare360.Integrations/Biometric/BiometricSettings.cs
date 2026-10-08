namespace HyFib.HealthCare360.Integrations.Biometric;

/// <summary>
/// Non-secret settings for reading attendance terminals (<c>integrations.biometric</c>, INT-BIO). Terminals that push
/// over ADMS or the punch API need none of this; it applies only to terminals HR sets to be read by HC360.
/// </summary>
internal sealed record BiometricSettings : IIntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary><c>zkteco</c> (the terminal's own protocol on TCP port 4370) or <c>simulator</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>How long one read of a terminal may take, connecting included.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Secret settings (<c>integrations.biometric.secret</c>).</summary>
internal sealed record BiometricSecrets
{
    /// <summary>
    /// The terminals' communication key ("COMM Key" in the terminal's menu, 0 to 999999). Empty when the terminals
    /// have none, which is how they leave the factory.
    /// </summary>
    public string? CommKey { get; init; }
}

internal interface IAttendanceTerminalProvider
{
    string Key { get; }

    Task<AttendanceTerminalRead> ReadAsync(AttendanceTerminal terminal, BiometricSettings settings, BiometricSecrets secrets, CancellationToken cancellationToken);

    Task<string?> SetClockAsync(AttendanceTerminal terminal, DateTime localTime, BiometricSettings settings, BiometricSecrets secrets, CancellationToken cancellationToken);
}
