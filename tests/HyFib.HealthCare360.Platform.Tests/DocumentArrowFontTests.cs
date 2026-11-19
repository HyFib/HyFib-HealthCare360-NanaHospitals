using System.Buffers.Binary;
using System.IO.Compression;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The up and down arrows a lab report prints beside a high or low flag (↑ H, ↓ L) render whatever fonts the server or
/// the reader's computer has: a document with either arrow carries its own font for them, and that font has both.
/// </summary>
public sealed class DocumentArrowFontTests
{
    [Fact]
    public void A_document_with_an_arrow_carries_the_arrows_font_and_one_without_does_not()
    {
        DocumentFonts.For("Potassium 6.1 H").ShouldBeEmpty();

        var up = DocumentFonts.For("Potassium 6.1 ↑ H");
        up.ShouldContain("@font-face { font-family: \"HC360 Arrows\"; font-style: normal; font-weight: 400; font-display: swap; unicode-range: U+2191, U+2193;");
        up.ShouldContain("font-weight: 700; font-display: swap; unicode-range: U+2191, U+2193;", Case.Sensitive, "flags print in bold");
        up.ShouldNotContain("HC360 Devanagari");
        DocumentFonts.For("Sodium 118 ↓ LL").ShouldBe(up);
        DocumentFonts.For("रोगी ↑").ShouldContain("HC360 Devanagari");
        DocumentFonts.For("रोगी ↑").ShouldContain("HC360 Arrows");
    }

    [Fact]
    public void A_printed_document_with_an_arrow_brings_its_font()
    {
        var html = new PrintableDocument { Title = "Lab report", FileName = "report", Body = Html.Text("Haemoglobin 5.1 ↓ LL") }.ToHtml();

        html.ShouldContain("@font-face { font-family: \"HC360 Arrows\"");
        html.ShouldContain("Haemoglobin 5.1 ↓ LL", Case.Sensitive, "the arrow is text in the page, not an entity or a picture");
    }

    [Theory]
    [InlineData("InterArrows-Regular.woff2")]
    [InlineData("InterArrows-Bold.woff2")]
    public void The_arrows_font_has_a_glyph_for_both_arrows(string file)
    {
        var characters = Woff2Characters(file);

        characters.ShouldContain(DocumentFonts.Up);
        characters.ShouldContain(DocumentFonts.Down);
    }

    /// <summary>
    /// The characters an embedded WOFF2 font maps to a glyph, read from its 'cmap' table: the tables are one Brotli stream,
    /// in directory order, and 'cmap' is never transformed. Reads the Unicode subtables of formats 4 and 12.
    /// </summary>
    private static HashSet<char> Woff2Characters(string file)
    {
        using var resource = typeof(DocumentFonts).Assembly.GetManifestResourceStream($"HyFib.HealthCare360.Platform.Documents.Fonts.{file}")
            .ShouldNotBeNull($"{file} is embedded");
        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        var font = copy.ToArray();
        BinaryPrimitives.ReadUInt32BigEndian(font).ShouldBe(0x774F4632u, "a WOFF2 font starts with wOF2");

        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(12));
        var compressedLength = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(20));
        var position = 48;
        var offset = 0;
        (int Offset, int Length)? cmap = null;
        for (var table = 0; table < tableCount; table++)
        {
            var flags = font[position++];
            var tagIndex = flags & 0x3F;
            var isCmap = tagIndex == 0;
            if (tagIndex == 63)
            {
                isCmap = BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(position)) == 0x636D6170u;
                position += 4;
            }

            var length = ReadBase128(font, ref position);
            var transform = flags >> 6;
            var transformed = tagIndex is 10 or 11 ? transform != 3 : transform != 0;
            if (transformed)
            {
                length = ReadBase128(font, ref position);
            }

            if (isCmap)
            {
                cmap = (offset, length);
            }

            offset += length;
        }

        using var tables = new MemoryStream();
        using (var brotli = new BrotliStream(new MemoryStream(font, position, compressedLength), CompressionMode.Decompress))
        {
            brotli.CopyTo(tables);
        }

        var (start, _) = cmap.ShouldNotBeNull("the font has a character map");
        return Characters(tables.ToArray().AsSpan(start));
    }

    private static HashSet<char> Characters(ReadOnlySpan<byte> cmap)
    {
        var characters = new HashSet<char>();
        var count = BinaryPrimitives.ReadUInt16BigEndian(cmap[2..]);
        for (var record = 0; record < count; record++)
        {
            var at = 4 + (record * 8);
            var subtable = cmap[(int)BinaryPrimitives.ReadUInt32BigEndian(cmap[(at + 4)..])..];
            switch (BinaryPrimitives.ReadUInt16BigEndian(subtable))
            {
                case 4:
                    var segments = BinaryPrimitives.ReadUInt16BigEndian(subtable[6..]) / 2;
                    for (var segment = 0; segment < segments; segment++)
                    {
                        var end = BinaryPrimitives.ReadUInt16BigEndian(subtable[(14 + (2 * segment))..]);
                        var begin = BinaryPrimitives.ReadUInt16BigEndian(subtable[(16 + (2 * segments) + (2 * segment))..]);
                        for (var c = begin; c <= end && c != 0xFFFF; c++)
                        {
                            characters.Add((char)c);
                        }
                    }

                    break;
                case 12:
                    var groups = BinaryPrimitives.ReadUInt32BigEndian(subtable[12..]);
                    for (var group = 0; group < groups; group++)
                    {
                        var first = BinaryPrimitives.ReadUInt32BigEndian(subtable[(16 + (12 * group))..]);
                        var last = BinaryPrimitives.ReadUInt32BigEndian(subtable[(20 + (12 * group))..]);
                        for (var c = first; c <= last && c <= char.MaxValue; c++)
                        {
                            characters.Add((char)c);
                        }
                    }

                    break;
            }
        }

        return characters;
    }

    /// <summary>WOFF2's UIntBase128: seven bits a byte, the high bit set on every byte but the last.</summary>
    private static int ReadBase128(byte[] data, ref int position)
    {
        var value = 0;
        for (var i = 0; i < 5; i++)
        {
            var b = data[position++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw new InvalidDataException("A WOFF2 number is longer than five bytes.");
    }
}
