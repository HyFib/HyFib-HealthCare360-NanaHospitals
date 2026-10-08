using System.Buffers.Binary;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Biometric;

/// <summary>A packet of the ZKTeco protocol: command (or reply code), session, reply number and the command's data.</summary>
internal sealed record ZkTecoPacket(ushort Command, ushort SessionId, ushort ReplyId, byte[] Data);

/// <summary>The terminal answered in a way HC360 cannot use. The message is for HR staff.</summary>
internal sealed class ZkTecoException(string message) : Exception(message);

/// <summary>
/// The protocol ZKTeco, eSSL and compatible attendance terminals speak on TCP port 4370: the one their own attendance
/// software and SDK use. Pure functions over bytes, so they are tested without a terminal. A TCP frame is
/// <c>50 50 82 7D</c>, the packet's length (uint32) and the packet: command, checksum, session id and reply number
/// (uint16 each, little-endian), then the command's data.
/// </summary>
internal static class ZkTecoProtocol
{
    public const int DefaultPort = 4370;

    public const ushort CmdOptionsRead = 11;
    public const ushort CmdAttLogRead = 13;
    public const ushort CmdGetFreeSizes = 50;
    public const ushort CmdGetTime = 201;
    public const ushort CmdSetTime = 202;
    public const ushort CmdConnect = 1000;
    public const ushort CmdExit = 1001;
    public const ushort CmdAuth = 1102;
    public const ushort CmdPrepareData = 1500;
    public const ushort CmdData = 1501;
    public const ushort CmdFreeData = 1502;
    public const ushort CmdPrepareBuffer = 1503;
    public const ushort CmdReadBuffer = 1504;
    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckUnauthorized = 2005;

    public const int HeaderLength = 8;
    public const int FrameTopLength = 8;

    /// <summary>The largest piece of a buffer asked for at once over TCP (as the terminals' own software does).</summary>
    public const int MaxChunk = 0xFFC0;

    private const ushort FrameMagic1 = 0x5050;
    private const ushort FrameMagic2 = 0x7D82;
    private const int UShortMax = ushort.MaxValue;

    /// <summary>40-byte records (firmware of the last decade) and 16-byte records (older firmware).</summary>
    public const int LongRecord = 40;
    public const int ShortRecord = 16;
    public const int OldestRecord = 8;

    /// <summary>The reply that accepts a command; data and the start of data count too.</summary>
    public static bool IsOk(ushort reply) => reply is AckOk or CmdPrepareData or CmdData;

    /// <summary>
    /// The packet for <paramref name="command"/>, and the reply number to send it with. As in the terminals' own SDK,
    /// the checksum covers the previous reply number and the packet carries the next one.
    /// </summary>
    public static byte[] Packet(ushort command, ushort sessionId, ushort replyId, ReadOnlySpan<byte> data)
    {
        var packet = new byte[HeaderLength + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), replyId);
        data.CopyTo(packet.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), Checksum(packet));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), NextReplyId(replyId));
        return packet;
    }

    public static ushort NextReplyId(ushort replyId) => (ushort)(replyId + 1 >= UShortMax ? replyId + 1 - UShortMax : replyId + 1);

    /// <summary>The frame that carries <paramref name="packet"/> over TCP.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> packet)
    {
        var frame = new byte[FrameTopLength + packet.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, FrameMagic1);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), FrameMagic2);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)packet.Length);
        packet.CopyTo(frame.AsSpan(FrameTopLength));
        return frame;
    }

    /// <summary>The length of the packet a frame top announces, or -1 when it is not a frame top.</summary>
    public static long FrameLength(ReadOnlySpan<byte> top) =>
        top.Length >= FrameTopLength
        && BinaryPrimitives.ReadUInt16LittleEndian(top) == FrameMagic1
        && BinaryPrimitives.ReadUInt16LittleEndian(top[2..]) == FrameMagic2
            ? BinaryPrimitives.ReadUInt32LittleEndian(top[4..])
            : -1;

    public static ZkTecoPacket ParsePacket(byte[] packet)
    {
        if (packet.Length < HeaderLength)
        {
            throw new ZkTecoException("The terminal sent a reply too short to read.");
        }

        return new ZkTecoPacket(
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6)),
            packet[HeaderLength..]);
    }

    /// <summary>The protocol's checksum: a ones'-complement style sum of little-endian 16-bit words.</summary>
    public static ushort Checksum(ReadOnlySpan<byte> packet)
    {
        long sum = 0;
        var i = 0;
        for (; i + 1 < packet.Length; i += 2)
        {
            sum += packet[i] | (packet[i + 1] << 8);
            if (sum > UShortMax)
            {
                sum -= UShortMax;
            }
        }

        if (i < packet.Length)
        {
            sum += packet[i];
        }

        while (sum > UShortMax)
        {
            sum -= UShortMax;
        }

        sum = ~sum;
        while (sum < 0)
        {
            sum += UShortMax;
        }

        return (ushort)sum;
    }

    /// <summary>
    /// The scrambled communication key a terminal with a COMM Key asks for after connecting: the key's bits reversed,
    /// plus the session id, mixed with "ZKSO" and a tick count, as the terminals' SDK does.
    /// </summary>
    public static byte[] CommKey(uint key, ushort sessionId, byte ticks = 50)
    {
        uint reversed = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            reversed = (key & (1u << bit)) != 0 ? (reversed << 1) | 1 : reversed << 1;
        }

        var k = unchecked(reversed + sessionId);
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, k);
        b[0] ^= (byte)'Z';
        b[1] ^= (byte)'K';
        b[2] ^= (byte)'S';
        b[3] ^= (byte)'O';

        // The two halves change places, then the tick count is mixed in and replaces the third byte.
        return [(byte)(b[2] ^ ticks), (byte)(b[3] ^ ticks), ticks, (byte)(b[1] ^ ticks)];
    }

    /// <summary>The data of a buffered read of <paramref name="command"/> (the prepare-buffer request).</summary>
    public static byte[] PrepareBufferRequest(ushort command)
    {
        var request = new byte[11];
        request[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(1), command);
        return request;
    }

    public static byte[] ReadBufferRequest(int start, int size)
    {
        var request = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(request, start);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(4), size);
        return request;
    }

    /// <summary>Terminal times are seconds counted in 31-day months and 12-month years from 2000.</summary>
    public static DateTime? DecodeTime(uint value)
    {
        var second = (int)(value % 60);
        value /= 60;
        var minute = (int)(value % 60);
        value /= 60;
        var hour = (int)(value % 24);
        value /= 24;
        var day = (int)(value % 31) + 1;
        value /= 31;
        var month = (int)(value % 12) + 1;
        var year = (int)(value / 12) + 2000;
        return day <= DateTime.DaysInMonth(year, month)
            ? new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified)
            : null;
    }

    public static uint EncodeTime(DateTime time) =>
        (uint)((((time.Year % 100) * 12 * 31) + ((time.Month - 1) * 31) + time.Day - 1) * 86_400
               + (((time.Hour * 60) + time.Minute) * 60) + time.Second);

    /// <summary>The value of an option the terminal reports as <c>~Name=value\0</c>, or null.</summary>
    public static string? OptionValue(ReadOnlySpan<byte> data)
    {
        var text = Encoding.ASCII.GetString(data);
        var end = text.IndexOf('\0', StringComparison.Ordinal);
        text = end >= 0 ? text[..end] : text;
        var equals = text.IndexOf('=', StringComparison.Ordinal);
        var value = (equals >= 0 ? text[(equals + 1)..] : string.Empty).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>How many attendance records the terminal holds (from its free-sizes reply), or null.</summary>
    public static int? RecordCount(ReadOnlySpan<byte> sizes) =>
        sizes.Length >= 80 ? BinaryPrimitives.ReadInt32LittleEndian(sizes[32..]) : null;

    /// <summary>
    /// The length of one record in an attendance buffer of <paramref name="total"/> bytes the terminal said holds
    /// <paramref name="count"/> records, or 0 when no known layout fits. A terminal can take punches between counting
    /// and reading, so a buffer a few records longer than counted is read by the layout that fits it.
    /// </summary>
    public static int RecordLength(int total, int count)
    {
        if (total <= 0)
        {
            return 0;
        }

        var exact = count > 0 && total % count == 0 ? total / count : 0;
        if (exact is OldestRecord or ShortRecord or LongRecord)
        {
            return exact;
        }

        foreach (var length in (int[])[LongRecord, ShortRecord])
        {
            if (total % length == 0 && total / length >= count)
            {
                return length;
            }
        }

        // Some firmware pads the 40-byte layout to a longer record.
        return exact > LongRecord ? exact : 0;
    }

    /// <summary>
    /// The attendance records in a buffer read with <see cref="CmdAttLogRead"/>: its length (uint32), then the
    /// records. Records whose time cannot be a date are counted in <paramref name="unreadable"/> and left out.
    /// </summary>
    public static IReadOnlyList<TerminalPunchRecord> AttendanceRecords(ReadOnlySpan<byte> buffer, int count, out int unreadable)
    {
        unreadable = 0;
        if (buffer.Length < 4)
        {
            return [];
        }

        var data = buffer[4..];
        var total = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(buffer), (uint)data.Length);
        data = data[..total];
        if (total == 0)
        {
            return [];
        }

        var length = RecordLength(total, count);
        if (length == OldestRecord)
        {
            throw new ZkTecoException(
                "This terminal's firmware keeps attendance in a form too old to read. Set it to send punches to HC360 (ADMS) instead, or update its firmware.");
        }

        if (length == 0)
        {
            throw new ZkTecoException("The terminal's attendance records are in a form HC360 does not know.");
        }

        var records = new List<TerminalPunchRecord>(total / length);
        for (var offset = 0; offset + length <= total; offset += length)
        {
            var record = data.Slice(offset, length);
            var parsed = length == ShortRecord ? ShortRecordOf(record) : LongRecordOf(record);
            if (parsed is null)
            {
                unreadable++;
            }
            else
            {
                records.Add(parsed);
            }
        }

        return records;
    }

    /// <summary>uid (2), user id (24, text), verification (1), time (4), punch state (1), reserved (8).</summary>
    private static TerminalPunchRecord? LongRecordOf(ReadOnlySpan<byte> record)
    {
        var id = record.Slice(2, 24);
        var end = id.IndexOf((byte)0);
        var pin = Encoding.ASCII.GetString(end >= 0 ? id[..end] : id).Trim();
        var time = DecodeTime(BinaryPrimitives.ReadUInt32LittleEndian(record[27..]));
        return pin.Length == 0 || time is null ? null : new TerminalPunchRecord(pin, time.Value, record[31], record[26], null);
    }

    /// <summary>User id (uint32), time (4), verification (1), punch state (1), reserved (2), work code (uint32).</summary>
    private static TerminalPunchRecord? ShortRecordOf(ReadOnlySpan<byte> record)
    {
        var pin = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var time = DecodeTime(BinaryPrimitives.ReadUInt32LittleEndian(record[4..]));
        var workCode = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        return pin == 0 || time is null
            ? null
            : new TerminalPunchRecord(pin.ToString(System.Globalization.CultureInfo.InvariantCulture), time.Value, record[9], record[8],
                workCode == 0 ? null : workCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
