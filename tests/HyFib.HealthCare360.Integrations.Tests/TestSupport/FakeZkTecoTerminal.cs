using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HyFib.HealthCare360.Integrations.Biometric;
using static HyFib.HealthCare360.Integrations.Biometric.ZkTecoProtocol;

namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>
/// An attendance terminal on a loopback port that speaks the ZKTeco protocol as terminals do: it checks every
/// packet's checksum, asks for the communication key when it has one, and sends its buffer either in the reply or as
/// announced data packets. It records the commands it was sent.
/// </summary>
internal sealed class FakeZkTecoTerminal : IAsyncDisposable
{
    private const ushort Session = 0x1234;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private readonly List<ushort> _commands = [];

    public FakeZkTecoTerminal()
    {
        _listener.Start();
        _serving = ServeAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string SerialNumber { get; set; } = "CQZ7232460163";

    public uint? CommKey { get; set; }

    public DateTime Clock { get; set; } = new(2026, 10, 8, 9, 30, 0);

    /// <summary>The attendance records, without the length in front.</summary>
    public byte[] Records { get; set; } = [];

    /// <summary>What the terminal says it holds; the record count of <see cref="Records"/> in 40-byte records when null.</summary>
    public int? RecordCount { get; set; }

    /// <summary>Sends the whole buffer in the reply to the prepare request, as terminals do with a small one.</summary>
    public bool SendBufferAtOnce { get; set; }

    /// <summary>Answers a connection with these bytes instead of the protocol (another kind of device).</summary>
    public byte[]? Banner { get; set; }

    public DateTime? ClockSetTo { get; private set; }

    public IReadOnlyList<ushort> Commands
    {
        get
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }
    }

    /// <summary>A 40-byte record: uid, user id (24, text), verification, time, punch state, reserved.</summary>
    public static byte[] LongRecord(ushort uid, string pin, DateTime time, byte punchState = 0, byte verify = 1)
    {
        var record = new byte[ZkTecoProtocol.LongRecord];
        BinaryPrimitives.WriteUInt16LittleEndian(record, uid);
        Encoding.ASCII.GetBytes(pin).CopyTo(record, 2);
        record[26] = verify;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(27), EncodeTime(time));
        record[31] = punchState;
        return record;
    }

    /// <summary>A 16-byte record: user id (number), time, verification, punch state, reserved, work code.</summary>
    public static byte[] ShortRecord(uint pin, DateTime time, byte punchState = 0, byte verify = 1, uint workCode = 0)
    {
        var record = new byte[ZkTecoProtocol.ShortRecord];
        BinaryPrimitives.WriteUInt32LittleEndian(record, pin);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), EncodeTime(time));
        record[8] = verify;
        record[9] = punchState;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), workCode);
        return record;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _serving;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        _stop.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            await using var stream = client.GetStream();
            try
            {
                await ConverseAsync(stream, _stop.Token);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException)
            {
                // The client hung up.
            }
        }
    }

    private async Task ConverseAsync(Stream stream, CancellationToken ct)
    {
        if (Banner is not null)
        {
            // Like a real service, it keeps the connection open and reads until the client hangs up. Closing it with the
            // client's request unread would reset the connection, and Windows then drops the banner before it is read.
            await stream.WriteAsync(Banner, ct);
            var ignored = new byte[256];
            while (await stream.ReadAsync(ignored, ct) > 0)
            {
            }

            return;
        }

        var authorised = CommKey is null;
        ushort reply = 0;
        while (true)
        {
            var top = new byte[FrameTopLength];
            await stream.ReadExactlyAsync(top, ct);
            var packet = new byte[FrameLength(top)];
            await stream.ReadExactlyAsync(packet, ct);
            CheckChecksum(packet);
            var request = ParsePacket(packet);
            lock (_commands)
            {
                _commands.Add(request.Command);
            }

            reply = request.ReplyId;
            async Task Send(ushort command, byte[] data) => await stream.WriteAsync(Frame(Reply(command, reply, data)), ct);

            switch (request.Command)
            {
                case CmdConnect:
                    await Send(authorised ? AckOk : AckUnauthorized, []);
                    break;
                case CmdAuth:
                    authorised = request.Data.SequenceEqual(ZkTecoProtocol.CommKey(CommKey!.Value, Session));
                    await Send(authorised ? AckOk : AckUnauthorized, []);
                    break;
                case CmdExit:
                    await Send(AckOk, []);
                    return;
                case var _ when !authorised:
                    await Send(AckUnauthorized, []);
                    break;
                case CmdOptionsRead:
                    await Send(AckOk, Encoding.ASCII.GetBytes($"~SerialNumber={SerialNumber}\0"));
                    break;
                case CmdGetTime:
                    await Send(AckOk, Time(Clock));
                    break;
                case CmdSetTime:
                    ClockSetTo = DecodeTime(BinaryPrimitives.ReadUInt32LittleEndian(request.Data));
                    await Send(AckOk, []);
                    break;
                case CmdGetFreeSizes:
                    var sizes = new byte[92];
                    BinaryPrimitives.WriteInt32LittleEndian(sizes.AsSpan(32), RecordCount ?? Records.Length / ZkTecoProtocol.LongRecord);
                    await Send(AckOk, sizes);
                    break;
                case CmdPrepareBuffer:
                    if (SendBufferAtOnce)
                    {
                        await Send(CmdData, Buffer());
                    }
                    else
                    {
                        var prepared = new byte[9];
                        BinaryPrimitives.WriteUInt32LittleEndian(prepared.AsSpan(1), (uint)Buffer().Length);
                        await Send(AckOk, prepared);
                    }

                    break;
                case CmdReadBuffer:
                    var start = BinaryPrimitives.ReadInt32LittleEndian(request.Data);
                    var size = BinaryPrimitives.ReadInt32LittleEndian(request.Data.AsSpan(4));
                    var chunk = Buffer().AsSpan(start, size).ToArray();
                    var announced = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(announced, (uint)chunk.Length);
                    await Send(CmdPrepareData, announced);

                    // The data comes in two packets, as terminals send a large piece.
                    var half = chunk.Length / 2;
                    await Send(CmdData, chunk[..half]);
                    await Send(CmdData, chunk[half..]);
                    await Send(AckOk, []);
                    break;
                case CmdFreeData:
                    await Send(AckOk, []);
                    break;
                default:
                    await Send(AckError, []);
                    break;
            }
        }
    }

    private byte[] Buffer()
    {
        var buffer = new byte[4 + Records.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)Records.Length);
        Records.CopyTo(buffer, 4);
        return buffer;
    }

    private static byte[] Time(DateTime time)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, EncodeTime(time));
        return data;
    }

    private static byte[] Reply(ushort command, ushort replyId, byte[] data)
    {
        var packet = new byte[HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), Session);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), replyId);
        data.CopyTo(packet, HeaderLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Checksum(packet));
        return packet;
    }

    /// <summary>The checksum covers the packet as it was with the previous reply number, as the terminals' SDK sends it.</summary>
    private static void CheckChecksum(byte[] packet)
    {
        var copy = packet.ToArray();
        var sent = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(2));
        var replyId = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(6));
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(6), (ushort)(replyId == 0 ? ushort.MaxValue - 1 : replyId - 1));
        if (Checksum(copy) != sent)
        {
            throw new InvalidOperationException("Bad checksum");
        }
    }
}
