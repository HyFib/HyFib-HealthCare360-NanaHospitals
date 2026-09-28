namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>Minimal Lower Layer Protocol: <c>&lt;VT&gt; message &lt;FS&gt;&lt;CR&gt;</c> (0x0B … 0x1C 0x0D).</summary>
internal static class Mllp
{
    public const byte StartBlock = 0x0B;
    public const byte EndBlock = 0x1C;
    public const byte CarriageReturn = 0x0D;

    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var framed = new byte[payload.Length + 3];
        framed[0] = StartBlock;
        payload.CopyTo(framed.AsSpan(1));
        framed[^2] = EndBlock;
        framed[^1] = CarriageReturn;
        return framed;
    }
}

/// <summary>
/// Incremental MLLP decoder for a TCP stream: bytes outside a block are ignored, the end marker may be split across
/// reads, a new start byte abandons an unterminated block, and a block larger than the limit is discarded and flagged
/// (<see cref="Overflowed"/>) so the session can close the connection instead of buffering without bound.
/// </summary>
internal sealed class MllpFrameReader(int maxMessageBytes)
{
    private readonly List<byte> _buffer = [];
    private bool _inBlock;
    private bool _pendingEnd;

    public bool Overflowed { get; private set; }

    public IReadOnlyList<byte[]> Append(ReadOnlySpan<byte> data)
    {
        List<byte[]>? messages = null;
        foreach (var value in data)
        {
            if (!_inBlock)
            {
                if (value == Mllp.StartBlock)
                {
                    _inBlock = true;
                    _pendingEnd = false;
                    _buffer.Clear();
                }

                continue;
            }

            if (_pendingEnd)
            {
                _pendingEnd = false;
                if (value == Mllp.CarriageReturn)
                {
                    (messages ??= []).Add([.. _buffer]);
                    _buffer.Clear();
                    _inBlock = false;
                    continue;
                }

                // A lone FS inside the payload: keep it as data.
                Add(Mllp.EndBlock);
            }

            if (value == Mllp.EndBlock)
            {
                _pendingEnd = true;
            }
            else if (value == Mllp.StartBlock)
            {
                _buffer.Clear();
            }
            else
            {
                Add(value);
            }
        }

        return messages ?? [];
    }

    private void Add(byte value)
    {
        if (!_inBlock)
        {
            return;
        }

        if (_buffer.Count >= maxMessageBytes)
        {
            Overflowed = true;
            _inBlock = false;
            _buffer.Clear();
            return;
        }

        _buffer.Add(value);
    }
}
