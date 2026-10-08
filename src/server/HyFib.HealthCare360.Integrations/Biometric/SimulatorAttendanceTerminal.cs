using System.Globalization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Simulator;

namespace HyFib.HealthCare360.Integrations.Biometric;

/// <summary>
/// Training terminal: HR punches on it from HR › Attendance terminals as an employee would at a real terminal, and
/// reading it brings the punches in, so the whole flow can be practised without a terminal. Like a real terminal it
/// keeps its punches, so every read returns them all (the last 90 days) and HR leaves out the ones it already has.
/// Its clock is the hospital's (India) time and cannot drift.
/// </summary>
internal sealed class SimulatorAttendanceTerminal(SimulatorInbox inbox, IntegrationTrace trace, TimeProvider time) : IAttendanceTerminalProvider
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Punch state 255: the terminal's in/out keys are not used, as on most terminals.</summary>
    private const int NoPunchState = 255;
    private const int Fingerprint = 1;

    private static readonly Lazy<TimeZoneInfo> India = new(() =>
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "India Standard Time", "India Standard Time");
        }
    });

    public string Key => ProviderKeys.Simulator;

    public async Task<AttendanceTerminalRead> ReadAsync(
        AttendanceTerminal terminal, BiometricSettings settings, BiometricSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var now = time.GetUtcNow();
        var punches = await inbox.TerminalPunchesAsync(terminal.FacilityId, terminal.SerialNumber, now.AddDays(-90), cancellationToken);
        var records = punches
            .Select(p => DateTime.TryParseExact(p.Description, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? new TerminalPunchRecord(p.Reference ?? string.Empty, at, NoPunchState, Fingerprint, null)
                : null)
            .OfType<TerminalPunchRecord>()
            .ToList();
        await trace.LogAsync(IntegrationNames.Biometric, Key, "terminal.read", true, started, terminal.SerialNumber, null, $"punches={records.Count}");
        return new AttendanceTerminalRead(true, null, LocalNow(), records);
    }

    /// <summary>The training terminal keeps the hospital's time, so there is nothing to set.</summary>
    public Task<string?> SetClockAsync(
        AttendanceTerminal terminal, DateTime localTime, BiometricSettings settings, BiometricSecrets secrets, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public async Task PunchAsync(AttendanceTerminal terminal, string pin, DateTime localTime, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        await inbox.AddTransactionAsync(new SimulatorTransaction
        {
            Kind = SimulatorTransactionKind.TerminalPunch,
            FacilityId = terminal.FacilityId,
            ExternalId = "punchsim_" + Guid.CreateVersion7().ToString("N")[..20],
            TerminalId = terminal.SerialNumber,
            Reference = pin,
            Description = localTime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Status = SimulatorTransactionStatus.Captured,
            CompletedAt = time.GetUtcNow(),
        }, cancellationToken);
        await trace.LogAsync(IntegrationNames.Biometric, Key, "terminal.training_punch", true, started, terminal.SerialNumber);
    }

    private DateTime LocalNow() => TimeZoneInfo.ConvertTime(time.GetUtcNow(), India.Value).DateTime;
}
