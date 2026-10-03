using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Pacs;

/// <summary>
/// Pictures for the training PACS: a greyscale PNG with rings whose size changes from image to image, so staff can see
/// the viewer move through a series. Made in memory with a minimal PNG writer (no imaging library).
/// </summary>
internal static class SimulatorImages
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>A square greyscale PNG, <paramref name="size"/> pixels a side (16–512), drawn from <paramref name="seed"/>.</summary>
    public static byte[] Png(int size, int seed)
    {
        var side = Math.Clamp(size, 16, 512);
        var rows = new byte[side * (side + 1)];
        var centre = side / 2.0;
        var spacing = 6 + (Math.Abs(seed) % 12);
        for (var y = 0; y < side; y++)
        {
            var row = y * (side + 1);
            rows[row] = 0; // filter: none
            for (var x = 0; x < side; x++)
            {
                var distance = Math.Sqrt(((x - centre) * (x - centre)) + ((y - centre) * (y - centre)));
                var ring = (int)(distance / spacing) % 2 == 0 ? 60 : 0;
                var shade = (int)(140 * (1 - Math.Min(1, distance / centre)));
                rows[row + 1 + x] = (byte)Math.Clamp(40 + shade + ring, 0, 255);
            }
        }

        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, side);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), side);
        header[8] = 8; // bits per sample
        header[9] = 0; // greyscale
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", Compress(rows));
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        png.Write(checksum);
    }

    private static uint Crc(uint crc, byte[] bytes)
    {
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
