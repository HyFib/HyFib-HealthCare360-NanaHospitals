using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// The last check of this server's clock against the national time servers: which one answered and how far off this
/// clock was (positive: this clock is behind), or why none could be asked.
/// </summary>
public sealed record ClockCheck(DateTimeOffset CheckedAt, string? Server, double? OffsetSeconds, string? Problem);

/// <summary>
/// This server's clock as last checked (<see cref="ServerClockChecker"/>). CERT-In's Directions of 28 April 2022, direction
/// (i), ask that ICT systems keep their clocks to the NTP servers of NIC or NPL: the audit trail, the logs and every record's
/// time are only evidence if the clock was right. Windows keeps the time (w32time); this check shows when it does not.
/// </summary>
public sealed class ServerClock
{
    private ClockCheck? _last;

    public ClockCheck? Last => Volatile.Read(ref _last);

    internal void Record(ClockCheck check) => Volatile.Write(ref _last, check);

    /// <summary>System health's warnings: a clock off by more than <paramref name="maxOffset"/>, or no time server answering.</summary>
    public static IEnumerable<HealthWarning> Warnings(ClockCheck? check, TimeSpan maxOffset)
    {
        if (check is null)
        {
            yield break;
        }

        if (check.OffsetSeconds is { } offset && Math.Abs(offset) > maxOffset.TotalSeconds)
        {
            yield return new HealthWarning("clock.offset", Math.Abs(offset) > 60 ? "critical" : "warning",
                $"This server's clock is {Math.Abs(offset):0.###} s {(offset > 0 ? "behind" : "ahead of")} {check.Server}. Record times, the audit trail " +
                "and the logs follow it: set Windows Time to synchronise with time.nplindia.org or samay1.nic.in (CERT-In, 28 April 2022).");
        }
        else if (check.OffsetSeconds is null)
        {
            yield return new HealthWarning("clock.unchecked", "warning",
                $"This server's clock could not be checked against the national time servers: {check.Problem} Allow NTP (UDP port 123) to " +
                "them, or list a time server of the hospital's that follows them under HealthCare360:TimeSync:Servers.");
        }
    }
}

/// <summary>A minimal SNTP client (RFC 4330): one request, the clock offset from the four timestamps.</summary>
public static class Sntp
{
    private static readonly DateTime Era0 = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How far this clock is from <paramref name="host"/>'s: positive when this clock is behind.</summary>
    public static async Task<TimeSpan> OffsetAsync(string host, TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
        return await OffsetAsync(new IPEndPoint(address, 123), time, timeout, ct);
    }

    public static async Task<TimeSpan> OffsetAsync(IPEndPoint endpoint, TimeProvider time, TimeSpan timeout, CancellationToken ct)
    {
        using var udp = new UdpClient(endpoint.AddressFamily);
        udp.Connect(endpoint);
        var request = new byte[48];
        request[0] = 0b00_100_011; // no leap warning, version 4, client
        var sent = time.GetUtcNow();
        Write(request.AsSpan(40), sent);
        await udp.SendAsync(request, ct);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        UdpReceiveResult reply;
        try
        {
            reply = await udp.ReceiveAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{endpoint} did not answer within {timeout.TotalSeconds:0} s.");
        }

        var received = time.GetUtcNow();
        var r = reply.Buffer;

        // A server's answer: mode 4, a stratum of 1–15, and our own transmit time echoed back (so it answers this request).
        if (r.Length < 48 || (r[0] & 0b111) != 4 || r[1] is 0 or > 15 || !r.AsSpan(24, 8).SequenceEqual(request.AsSpan(40, 8)))
        {
            throw new InvalidDataException($"{endpoint} sent an answer that is not a time server's.");
        }

        var serverReceived = Read(r.AsSpan(32));
        var serverSent = Read(r.AsSpan(40));
        return ((serverReceived - sent) + (serverSent - received)) / 2;
    }

    /// <summary>An NTP timestamp: seconds since 1900 and a binary fraction (the era that began in 2036 included).</summary>
    internal static void Write(Span<byte> target, DateTimeOffset at)
    {
        var ticks = at.UtcDateTime.Ticks - Era0.Ticks;
        var seconds = (ulong)(ticks / TimeSpan.TicksPerSecond);
        var fraction = (ulong)(ticks % TimeSpan.TicksPerSecond) * 0x1_0000_0000UL / TimeSpan.TicksPerSecond;
        BinaryPrimitives.WriteUInt32BigEndian(target, (uint)seconds);
        BinaryPrimitives.WriteUInt32BigEndian(target[4..], (uint)fraction);
    }

    internal static DateTimeOffset Read(ReadOnlySpan<byte> source)
    {
        ulong seconds = BinaryPrimitives.ReadUInt32BigEndian(source);
        ulong fraction = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);

        // Seconds with the top bit clear are of the era that begins on 7 February 2036.
        if (seconds < 0x8000_0000UL)
        {
            seconds += 0x1_0000_0000UL;
        }

        var ticks = (long)seconds * TimeSpan.TicksPerSecond + (long)(fraction * TimeSpan.TicksPerSecond / 0x1_0000_0000UL);
        return new DateTimeOffset(Era0.Ticks + ticks, TimeSpan.Zero);
    }
}

/// <summary>Checks this server's clock against <see cref="TimeSyncOptions.ServersToAsk"/> every <see cref="TimeSyncOptions.CheckInterval"/>, on every node.</summary>
internal sealed class ServerClockChecker(ServerClock clock, IOptions<PlatformOptions> options, TimeProvider time, ILogger<ServerClockChecker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.TimeSync;
        if (!settings.Enabled)
        {
            return;
        }

        var interval = settings.CheckInterval < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : settings.CheckInterval;
        do
        {
            var check = await CheckAsync(settings.ServersToAsk, stoppingToken);
            clock.Record(check);
            foreach (var warning in ServerClock.Warnings(check, settings.MaxOffset))
            {
                logger.LogWarning("{Code}: {Message}", warning.Code, warning.Message);
            }
        }
        while (await Delay(interval, stoppingToken));
    }

    internal async Task<ClockCheck> CheckAsync(IReadOnlyList<string> servers, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var server in servers)
        {
            try
            {
                var offset = await Sntp.OffsetAsync(server, time, Timeout, ct);
                return new ClockCheck(time.GetUtcNow(), server, Math.Round(offset.TotalSeconds, 3), null);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or InvalidDataException or IOException)
            {
                problems.Add($"{server}: {ex.Message}");
            }
        }

        return new ClockCheck(time.GetUtcNow(), null, null, string.Join("; ", problems) + ".");
    }

    private async Task<bool> Delay(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval, time, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

internal static class ServerClockRegistration
{
    public static IServiceCollection AddServerClock(this IServiceCollection services)
    {
        services.AddSingleton<ServerClock>();
        services.AddHostedService<ServerClockChecker>();
        return services;
    }
}
