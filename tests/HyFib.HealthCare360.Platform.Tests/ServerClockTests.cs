using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The server clock's check against the national time servers (CERT-In, 28 April 2022): an SNTP client that works the
/// offset out from the four timestamps, against a time server on this machine whose clock is set ahead.
/// </summary>
public sealed class ServerClockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_offset_is_how_far_the_time_servers_clock_is_ahead()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var answering = AnswerAsync(server, TimeSpan.FromSeconds(5), echo: true);

        var offset = await Sntp.OffsetAsync((IPEndPoint)server.Client.LocalEndPoint!, TimeProvider.System, TimeSpan.FromSeconds(3), Ct);
        await answering;

        offset.TotalSeconds.ShouldBe(5, 0.05);
    }

    [Fact]
    public async Task An_answer_that_is_not_to_this_request_is_refused()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var answering = AnswerAsync(server, TimeSpan.Zero, echo: false);

        await Should.ThrowAsync<InvalidDataException>(() =>
            Sntp.OffsetAsync((IPEndPoint)server.Client.LocalEndPoint!, TimeProvider.System, TimeSpan.FromSeconds(3), Ct));
        await answering;
    }

    [Fact]
    public async Task A_server_that_does_not_answer_times_out()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));

        await Should.ThrowAsync<TimeoutException>(() =>
            Sntp.OffsetAsync((IPEndPoint)server.Client.LocalEndPoint!, TimeProvider.System, TimeSpan.FromMilliseconds(300), Ct));
    }

    [Theory]
    [InlineData(0.2, null)]
    [InlineData(-3.5, "warning")]
    [InlineData(90, "critical")]
    public void A_clock_off_by_more_than_allowed_is_a_warning(double offset, string? severity)
    {
        var warnings = ServerClock.Warnings(new ClockCheck(DateTimeOffset.UtcNow, "time.nplindia.org", offset, null), TimeSpan.FromSeconds(1)).ToList();

        warnings.Select(w => w.Severity).ShouldBe(severity is null ? [] : [severity]);
    }

    [Fact]
    public void A_clock_that_could_not_be_checked_says_why()
    {
        var warning = ServerClock.Warnings(new ClockCheck(DateTimeOffset.UtcNow, null, null, "time.nplindia.org: timed out."), TimeSpan.FromSeconds(1))
            .ShouldHaveSingleItem();
        warning.Code.ShouldBe("clock.unchecked");
        warning.Message.ShouldContain("time.nplindia.org: timed out.");
    }

    [Fact]
    public void Timestamps_of_the_era_after_2036_read_back()
    {
        var at = new DateTimeOffset(2040, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var bytes = new byte[8];
        Sntp.Write(bytes, at);
        (Sntp.Read(bytes) - at).Duration().ShouldBeLessThan(TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Answers one request as a stratum 1 server whose clock is <paramref name="ahead"/>.</summary>
    private static async Task AnswerAsync(UdpClient server, TimeSpan ahead, bool echo)
    {
        var request = await server.ReceiveAsync(Ct);
        var now = DateTimeOffset.UtcNow + ahead;
        var reply = new byte[48];
        reply[0] = 0b00_100_100; // version 4, server
        reply[1] = 1;
        if (echo)
        {
            request.Buffer.AsSpan(40, 8).CopyTo(reply.AsSpan(24));
        }

        Sntp.Write(reply.AsSpan(32), now);
        Sntp.Write(reply.AsSpan(40), now);
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(12), 0x4C4F434C); // "LOCL"
        await server.SendAsync(reply, request.RemoteEndPoint, Ct);
    }
}
