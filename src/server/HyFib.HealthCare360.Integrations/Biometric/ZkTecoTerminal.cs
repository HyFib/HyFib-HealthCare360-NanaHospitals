using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace HyFib.HealthCare360.Integrations.Biometric;

/// <summary>
/// Reads ZKTeco, eSSL and compatible terminals over their own protocol on TCP port 4370, for terminals that cannot
/// push to HC360. It asks only for the serial number, the clock, the number of punches and the attendance records:
/// users, fingerprints and faces are never read, and nothing is deleted from the terminal. A terminal that reports a
/// serial number other than the registered one is not read, so punches are never put on the wrong terminal.
/// </summary>
internal sealed class ZkTecoTerminal(IntegrationTrace trace) : IAttendanceTerminalProvider
{
    public string Key => ProviderKeys.ZkTeco;

    public async Task<AttendanceTerminalRead> ReadAsync(
        AttendanceTerminal terminal, BiometricSettings settings, BiometricSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var (read, unreadable) = await RunAsync(terminal, settings, secrets, async (session, ct) =>
        {
            await CheckSerialAsync(session, terminal, ct);
            var clock = await session.ClockAsync(ct);
            var count = await session.RecordCountAsync(ct);
            if (count == 0)
            {
                return (new AttendanceTerminalRead(true, null, clock, []), 0);
            }

            var records = ZkTecoProtocol.AttendanceRecords(await session.AttendanceBufferAsync(ct), count, out var skipped);
            return (new AttendanceTerminalRead(true, null, clock, records), skipped);
        }, cancellationToken);

        await trace.LogAsync(IntegrationNames.Biometric, Key, "terminal.read", read.Success, started, terminal.SerialNumber,
            Target(terminal), read.Success ? $"punches={read.Records.Count} unreadable={unreadable}" : null, read.Error);
        return read;
    }

    public async Task<string?> SetClockAsync(
        AttendanceTerminal terminal, DateTime localTime, BiometricSettings settings, BiometricSecrets secrets, CancellationToken cancellationToken)
    {
        var started = trace.Start();
        var (result, _) = await RunAsync(terminal, settings, secrets, async (session, ct) =>
        {
            await CheckSerialAsync(session, terminal, ct);
            await session.SetClockAsync(localTime, ct);
            return (new AttendanceTerminalRead(true, null, localTime, []), 0);
        }, cancellationToken);

        await trace.LogAsync(IntegrationNames.Biometric, Key, "terminal.set_clock", result.Success, started, terminal.SerialNumber,
            Target(terminal), result.Success ? localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : null, result.Error);
        return result.Error;
    }

    /// <summary>Runs <paramref name="work"/> in one session, within the configured time, and turns failures into words.</summary>
    private static async Task<(AttendanceTerminalRead Read, int Unreadable)> RunAsync(
        AttendanceTerminal terminal,
        BiometricSettings settings,
        BiometricSecrets secrets,
        Func<ZkTecoSession, CancellationToken, Task<(AttendanceTerminalRead, int)>> work,
        CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(terminal.Host, out var address))
        {
            return (AttendanceTerminalRead.Failed("Enter the terminal's IP address in HR › Attendance terminals."), 0);
        }

        if (!TryCommKey(secrets.CommKey, out var commKey))
        {
            return (AttendanceTerminalRead.Failed(
                "The communication key in Administration › Integrations › Biometric attendance must be a number from 0 to 999999."), 0);
        }

        var seconds = Math.Clamp(settings.TimeoutSeconds, 5, 300);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try
        {
            await using var session = await ZkTecoSession.ConnectAsync(address, terminal.Port, commKey, timeout.Token);
            return await work(session, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (AttendanceTerminalRead.Failed($"The terminal at {Target(terminal)} did not answer within {seconds} seconds."), 0);
        }
        catch (SocketException ex)
        {
            return (AttendanceTerminalRead.Failed(ex.SocketErrorCode == SocketError.ConnectionRefused
                ? $"The terminal at {Target(terminal)} refused the connection. Check the address and that the terminal's TCP port is {terminal.Port}."
                : $"Could not reach the terminal at {Target(terminal)}. Check that it is switched on and on the network."), 0);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            return (AttendanceTerminalRead.Failed($"The connection to the terminal at {Target(terminal)} was lost part way. It is tried again at the next read."), 0);
        }
        catch (ZkTecoException ex)
        {
            return (AttendanceTerminalRead.Failed(ex.Message), 0);
        }
    }

    private static async Task CheckSerialAsync(ZkTecoSession session, AttendanceTerminal terminal, CancellationToken cancellationToken)
    {
        var serial = await session.SerialNumberAsync(cancellationToken);
        if (serial is not null && !string.Equals(serial, terminal.SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            throw new ZkTecoException(
                $"The terminal at {Target(terminal)} has serial number {Printable(serial)}, not {terminal.SerialNumber}. Check the address registered for this terminal.");
        }
    }

    /// <summary>An empty key means none; otherwise the number the terminal's menu shows (0–999999).</summary>
    internal static bool TryCommKey(string? text, out uint? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (uint.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= 999_999)
        {
            key = value;
            return true;
        }

        return false;
    }

    private static string Target(AttendanceTerminal terminal) => $"{terminal.Host}:{terminal.Port}";

    /// <summary>A serial number reported by the device, shown only if it looks like one.</summary>
    private static string Printable(string serial) =>
        serial.Length <= 64 && serial.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? serial : "(unreadable)";
}
