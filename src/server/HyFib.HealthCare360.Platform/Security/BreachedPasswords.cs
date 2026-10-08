using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Passwords published from data breaches (SRS SEC-01, ASVS V6.2.12), checked offline: nothing about a password leaves
/// the server. The embedded set holds about 800,000 breached passwords, lower-cased: every one of 12 characters or more in
/// the 10 million most common, and the most common of 8 to 11 characters for sites that lower the minimum length (see
/// <c>tools/build-breached-passwords.cs</c> for the source and the rules).
/// <para>
/// It is a Golomb-coded set: each password's fingerprint (the first 8 bytes of the SHA-256 of its lower-cased UTF-8),
/// reduced modulo <c>count × 2^bits</c>, sorted, and stored as Rice-coded gaps. About 22 bits a password; a password that
/// is not in it matches by chance about once in a million (2^-20).
/// </para>
/// </summary>
public sealed class BreachedPasswords
{
    private const string ResourceName = "HyFib.HealthCare360.Platform.Security.Resources.breached-passwords.gcs";
    private const int HeaderLength = 21;
    private static readonly byte[] Magic = "HC360BP1"u8.ToArray();
    private static readonly Lazy<BreachedPasswords> EmbeddedSet = new(LoadEmbedded);

    private readonly ulong[] _values;
    private readonly ulong _range;

    private BreachedPasswords(ulong[] values, ulong range)
    {
        _values = values;
        _range = range;
    }

    /// <summary>The set built into HC360 (read once, on first use).</summary>
    public static BreachedPasswords Embedded => EmbeddedSet.Value;

    /// <summary>How many fingerprints the set holds.</summary>
    public int Count => _values.Length;

    /// <summary>Whether the password, in any letter case, is in the set.</summary>
    public bool Contains(string password) =>
        _range > 0 && Array.BinarySearch(_values, Fingerprint(password) % _range) >= 0;

    /// <summary>Writes a set of the passwords, lower-cased and without duplicates.</summary>
    public static void Write(Stream output, IEnumerable<string> passwords, int bits = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 32);
        var distinct = passwords.Select(p => p.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var range = (ulong)distinct.Count << bits;
        var values = distinct.Select(p => Fingerprint(p) % range).Distinct().Order().ToArray();

        var header = new byte[HeaderLength];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), values.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(12), range);
        header[20] = (byte)bits;
        output.Write(header);

        var writer = new BitWriter(output);
        var previous = 0UL;
        foreach (var value in values)
        {
            var gap = value - previous;
            for (var quotient = gap >> bits; quotient > 0; quotient--)
            {
                writer.Write(true);
            }

            writer.Write(false);
            for (var bit = bits - 1; bit >= 0; bit--)
            {
                writer.Write(((gap >> bit) & 1) == 1);
            }

            previous = value;
        }

        writer.Flush();
    }

    /// <summary>Reads a set written by <see cref="Write"/>.</summary>
    public static BreachedPasswords Read(Stream input)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (data.Length < HeaderLength || !data[..Magic.Length].SequenceEqual(Magic))
        {
            throw new InvalidDataException("This is not an HC360 breached-password set.");
        }

        var count = BinaryPrimitives.ReadInt32BigEndian(data[8..]);
        var range = BinaryPrimitives.ReadUInt64BigEndian(data[12..]);
        int bits = data[20];
        if (count < 0 || bits is < 1 or > 32)
        {
            throw new InvalidDataException("The breached-password set's header is not valid.");
        }

        var values = new ulong[count];
        var reader = new BitReader(data[HeaderLength..]);
        var previous = 0UL;
        for (var i = 0; i < count; i++)
        {
            var quotient = 0UL;
            while (reader.Read())
            {
                quotient++;
            }

            var remainder = 0UL;
            for (var bit = 0; bit < bits; bit++)
            {
                remainder = (remainder << 1) | (reader.Read() ? 1UL : 0UL);
            }

            previous += (quotient << bits) | remainder;
            values[i] = previous;
        }

        return new BreachedPasswords(values, range);
    }

    internal static ulong Fingerprint(string password) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(password.ToLowerInvariant())));

    private static BreachedPasswords LoadEmbedded()
    {
        using var stream = typeof(BreachedPasswords).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The breached-password set ({ResourceName}) is missing from the build.");
        return Read(stream);
    }

    private sealed class BitWriter(Stream output)
    {
        private int _current;
        private int _used;

        public void Write(bool bit)
        {
            _current = (_current << 1) | (bit ? 1 : 0);
            if (++_used == 8)
            {
                output.WriteByte((byte)_current);
                _current = 0;
                _used = 0;
            }
        }

        public void Flush()
        {
            if (_used > 0)
            {
                output.WriteByte((byte)(_current << (8 - _used)));
                _current = 0;
                _used = 0;
            }
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _position;

        public bool Read()
        {
            var index = (int)(_position >> 3);
            if (index >= _data.Length)
            {
                throw new InvalidDataException("The breached-password set ends early.");
            }

            var bit = (_data[index] >> (7 - (int)(_position & 7))) & 1;
            _position++;
            return bit == 1;
        }
    }
}
