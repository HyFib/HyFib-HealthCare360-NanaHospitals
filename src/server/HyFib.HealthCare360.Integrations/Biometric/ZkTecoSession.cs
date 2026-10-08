using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using static HyFib.HealthCare360.Integrations.Biometric.ZkTecoProtocol;

namespace HyFib.HealthCare360.Integrations.Biometric;

/// <summary>
/// One conversation with a terminal over TCP: connect (and give the communication key when it asks), then read-only
/// commands, then exit. Each command waits for its reply; replies are read frame by frame by their announced length,
/// and a buffer larger than <see cref="MaxBufferBytes"/> is refused.
/// </summary>
internal sealed class ZkTecoSession : IAsyncDisposable
{
    /// <summary>About 400,000 punches in 40-byte records: far more than a terminal holds.</summary>
    public const int MaxBufferBytes = 16 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private ushort _sessionId;
    private ushort _replyId = ushort.MaxValue - 1;
    private bool _connected;

    private ZkTecoSession(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    /// <param name="commKey">The terminals' COMM Key, or null when none is set in HC360.</param>
    public static async Task<ZkTecoSession> ConnectAsync(IPAddress address, int port, uint? commKey, CancellationToken cancellationToken)
    {
        var client = new TcpClient(address.AddressFamily) { NoDelay = true };
        try
        {
            await client.ConnectAsync(address, port, cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var session = new ZkTecoSession(client);
        try
        {
            var reply = await session.SendAsync(CmdConnect, [], cancellationToken);
            session._sessionId = reply.SessionId;
            if (reply.Command == AckUnauthorized)
            {
                if (commKey is null)
                {
                    throw new ZkTecoException(
                        "The terminal asks for a communication key (COMM Key). Enter it in Administration › Integrations › Biometric attendance.");
                }

                reply = await session.SendAsync(CmdAuth, CommKey(commKey.Value, session._sessionId), cancellationToken);
                if (reply.Command == AckUnauthorized)
                {
                    throw new ZkTecoException(
                        "The terminal refused the communication key. Check the COMM Key in Administration › Integrations › Biometric attendance against the terminal's menu.");
                }
            }

            if (!IsOk(reply.Command))
            {
                throw new ZkTecoException("The terminal refused the connection.");
            }

            session._connected = true;
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>The serial number the terminal reports, or null when it does not say.</summary>
    public async Task<string?> SerialNumberAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync(CmdOptionsRead, "~SerialNumber\0"u8.ToArray(), cancellationToken);
        return IsOk(reply.Command) ? OptionValue(reply.Data) : null;
    }

    public async Task<DateTime?> ClockAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync(CmdGetTime, [], cancellationToken);
        return IsOk(reply.Command) && reply.Data.Length >= 4 ? DecodeTime(BinaryPrimitives.ReadUInt32LittleEndian(reply.Data)) : null;
    }

    public async Task SetClockAsync(DateTime localTime, CancellationToken cancellationToken)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, EncodeTime(localTime));
        var reply = await SendAsync(CmdSetTime, data, cancellationToken);
        if (!IsOk(reply.Command))
        {
            throw new ZkTecoException("The terminal did not accept the new time.");
        }
    }

    /// <summary>How many attendance records the terminal holds.</summary>
    public async Task<int> RecordCountAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync(CmdGetFreeSizes, [], cancellationToken);
        return IsOk(reply.Command) && RecordCount(reply.Data) is { } count
            ? count
            : throw new ZkTecoException("The terminal did not say how many punches it holds.");
    }

    /// <summary>Every attendance record, read in pieces through the terminal's buffer, which is freed afterwards.</summary>
    public async Task<byte[]> AttendanceBufferAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync(CmdPrepareBuffer, PrepareBufferRequest(CmdAttLogRead), cancellationToken);
        if (reply.Command == CmdData)
        {
            return reply.Data;
        }

        if (reply.Command != AckOk || reply.Data.Length < 5)
        {
            throw new ZkTecoException("The terminal would not prepare its punches for reading.");
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(reply.Data.AsSpan(1));
        if (size > MaxBufferBytes)
        {
            throw new ZkTecoException("The terminal holds more punches than HC360 reads at once. Clear its old attendance records on the terminal, after they have reached HC360.");
        }

        var buffer = new byte[size];
        for (var start = 0; start < size;)
        {
            var chunk = await ChunkAsync(start, (int)Math.Min(MaxChunk, size - start), cancellationToken);
            chunk.CopyTo(buffer.AsSpan(start));
            start += chunk.Length;
        }

        await SendAsync(CmdFreeData, [], cancellationToken);
        return buffer;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connected)
        {
            _connected = false;
            try
            {
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync(CmdExit, [], exit.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ZkTecoException or ObjectDisposedException)
            {
                // The terminal closes idle connections itself; a missed goodbye costs nothing.
            }
        }

        await _stream.DisposeAsync();
        _client.Dispose();
    }

    /// <summary>One piece of the prepared buffer: in the reply itself, or announced and sent as data packets.</summary>
    private async Task<byte[]> ChunkAsync(int start, int size, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(CmdReadBuffer, ReadBufferRequest(start, size), cancellationToken);
        if (reply.Command == CmdData)
        {
            return Checked(reply.Data, size);
        }

        if (reply.Command != CmdPrepareData || reply.Data.Length < 4)
        {
            throw new ZkTecoException("The terminal stopped sending its punches part way.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(reply.Data);
        if (expected > size)
        {
            throw new ZkTecoException("The terminal sent more than was asked for.");
        }

        using var data = new MemoryStream((int)expected);
        while (data.Length < expected)
        {
            var packet = await ReceiveAsync(cancellationToken);
            if (packet.Command != CmdData)
            {
                throw new ZkTecoException("The terminal stopped sending its punches part way.");
            }

            data.Write(packet.Data);
        }

        if ((await ReceiveAsync(cancellationToken)).Command != AckOk)
        {
            throw new ZkTecoException("The terminal did not confirm the end of its punches.");
        }

        return Checked(data.ToArray(), size);
    }

    private static byte[] Checked(byte[] chunk, int size) =>
        chunk.Length == size ? chunk : throw new ZkTecoException("The terminal sent a different amount of data than was asked for.");

    private async Task<ZkTecoPacket> SendAsync(ushort command, byte[] data, CancellationToken cancellationToken)
    {
        var packet = Packet(command, _sessionId, _replyId, data);
        await _stream.WriteAsync(Frame(packet), cancellationToken);
        var reply = await ReceiveAsync(cancellationToken);
        _replyId = reply.ReplyId;
        return reply;
    }

    private async Task<ZkTecoPacket> ReceiveAsync(CancellationToken cancellationToken)
    {
        var top = new byte[FrameTopLength];
        await _stream.ReadExactlyAsync(top, cancellationToken);
        var length = FrameLength(top);
        if (length is < HeaderLength or > MaxBufferBytes + HeaderLength)
        {
            throw new ZkTecoException(length < 0
                ? "The device at this address does not answer like an attendance terminal."
                : "The terminal sent a reply HC360 cannot read.");
        }

        var packet = new byte[length];
        await _stream.ReadExactlyAsync(packet, cancellationToken);
        return ParsePacket(packet);
    }
}
