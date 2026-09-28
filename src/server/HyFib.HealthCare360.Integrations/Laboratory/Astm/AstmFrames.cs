using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>ASTM E1381 / CLSI LIS01-A2 control characters.</summary>
internal static class AstmControl
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;
    public const byte Eot = 0x04;
    public const byte Enq = 0x05;
    public const byte Ack = 0x06;
    public const byte Lf = 0x0A;
    public const byte Cr = 0x0D;
    public const byte Nak = 0x15;
    public const byte Etb = 0x17;
}

/// <summary>A validated frame: its number (0–7), text and whether it ends a record (ETX) or continues it (ETB).</summary>
internal readonly record struct AstmFrame(int FrameNumber, string Text, bool IsFinal);

/// <summary>
/// E1381 frames: <c>&lt;STX&gt; FN text &lt;ETX|ETB&gt; C1 C2 &lt;CR&gt;&lt;LF&gt;</c>. The checksum is the sum of every
/// byte from the frame number through ETX/ETB, modulo 256, written as two uppercase hex digits.
/// </summary>
internal static class AstmFrames
{
    /// <summary>E1381 caps frame text at 240 characters; longer records continue in ETB frames.</summary>
    public const int MaxTextLength = 240;

    public static string Checksum(ReadOnlySpan<byte> frameNumberThroughTerminator)
    {
        var sum = 0;
        foreach (var value in frameNumberThroughTerminator)
        {
            sum = (sum + value) & 0xFF;
        }

        return sum.ToString("X2", CultureInfo.InvariantCulture);
    }

    public static byte[] Build(int frameNumber, string text, bool isFinal, Encoding encoding)
    {
        var body = encoding.GetBytes(text);
        var frame = new byte[body.Length + 7];
        frame[0] = AstmControl.Stx;
        frame[1] = (byte)('0' + (frameNumber % 8));
        body.CopyTo(frame, 2);
        var terminator = 2 + body.Length;
        frame[terminator] = isFinal ? AstmControl.Etx : AstmControl.Etb;
        var checksum = Checksum(frame.AsSpan(1, body.Length + 2));
        frame[terminator + 1] = (byte)checksum[0];
        frame[terminator + 2] = (byte)checksum[1];
        frame[terminator + 3] = AstmControl.Cr;
        frame[terminator + 4] = AstmControl.Lf;
        return frame;
    }

    /// <summary>Validates structure, frame number and checksum of a complete frame (STX … LF).</summary>
    public static bool TryParse(ReadOnlySpan<byte> frame, Encoding encoding, out AstmFrame parsed, out string? error)
    {
        parsed = default;
        if (frame.Length < 7 || frame[0] != AstmControl.Stx || frame[^1] != AstmControl.Lf || frame[^2] != AstmControl.Cr)
        {
            error = "malformed frame";
            return false;
        }

        var terminatorIndex = frame.Length - 5;
        var terminator = frame[terminatorIndex];
        if (terminator is not (AstmControl.Etx or AstmControl.Etb))
        {
            error = "missing ETX/ETB";
            return false;
        }

        var frameNumber = frame[1] - '0';
        if (frameNumber is < 0 or > 7)
        {
            error = "invalid frame number";
            return false;
        }

        var expected = Checksum(frame[1..(terminatorIndex + 1)]);
        var provided = Encoding.ASCII.GetString(frame.Slice(terminatorIndex + 1, 2));
        if (!string.Equals(expected, provided, StringComparison.OrdinalIgnoreCase))
        {
            error = $"checksum mismatch (expected {expected}, received {provided})";
            return false;
        }

        parsed = new AstmFrame(frameNumber, encoding.GetString(frame[2..terminatorIndex]), terminator == AstmControl.Etx);
        error = null;
        return true;
    }

    /// <summary>
    /// Frames for a whole message: each record ends with CR and starts a new frame; records longer than
    /// <see cref="MaxTextLength"/> are split with ETB frames. Frame numbers start at 1 and wrap modulo 8.
    /// </summary>
    public static IReadOnlyList<byte[]> BuildMessage(IReadOnlyList<string> records, Encoding encoding)
    {
        var frames = new List<byte[]>();
        var number = 1;
        foreach (var record in records)
        {
            var text = record + "\r";
            for (var offset = 0; offset < text.Length; offset += MaxTextLength)
            {
                var length = Math.Min(MaxTextLength, text.Length - offset);
                frames.Add(Build(number, text.Substring(offset, length), offset + length >= text.Length, encoding));
                number = (number + 1) % 8;
            }
        }

        return frames;
    }
}
