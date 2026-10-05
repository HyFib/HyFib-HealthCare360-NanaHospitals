using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Catalogue;

/// <summary>
/// Bounds that keep a broken or hostile file from using up the server's memory or time. The defaults fit the real files
/// with room to spare: WHO's ICD-10 2019 ClaML is a few tens of MB of XML (a few MB zipped) with about 12,000 categories,
/// and the CMS ICD-10-PCS order file about 80,000 lines.
/// </summary>
public sealed record CatalogueFileLimits
{
    public const long DefaultMaxUploadBytes = 100L * 1024 * 1024;

    /// <summary>The file as uploaded (zip, XML or text).</summary>
    public long MaxUploadBytes { get; init; } = DefaultMaxUploadBytes;

    /// <summary>The XML or text inside a zip, after decompression; checked while reading, not only from the zip's header.</summary>
    public long MaxUnzippedBytes { get; init; } = 300L * 1024 * 1024;

    public int MaxZipEntries { get; init; } = 100;

    /// <summary>ClaML <c>Class</c> elements (chapters, blocks and categories).</summary>
    public int MaxClasses { get; init; } = 60_000;

    public int MaxModifierClasses { get; init; } = 20_000;

    /// <summary>ICD-10 codes after the shared fourth and fifth characters are added, or PCS codes.</summary>
    public int MaxCodes { get; init; } = 200_000;

    /// <summary>
    /// A real WHO ICD-10 file has about 12,000 categories. Fewer than this means a sample, a damaged file or the wrong
    /// classification, which must not withdraw most of the catalogue.
    /// </summary>
    public int MinIcd10Categories { get; init; } = 5_000;

    /// <summary>A real CMS order file has about 80,000 valid codes (see <see cref="MinIcd10Categories"/>).</summary>
    public int MinPcsCodes { get; init; } = 40_000;

    public int MaxPcsLineLength { get; init; } = 1_000;

    /// <summary>Limits for tests that read small hand-made samples.</summary>
    public static CatalogueFileLimits ForSamples { get; } = new() { MinIcd10Categories = 1, MinPcsCodes = 1 };
}

/// <summary>A file HC360 cannot import, with a message an administrator can act on.</summary>
public sealed class CatalogueFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What a catalogue file held: the WHO ICD-10 classification or the CMS ICD-10-PCS codes.</summary>
public sealed record CatalogueFileContent(CatalogueKind Kind, string? InnerFileName, ClamlClassification? Icd10, PcsOrderFile? Pcs);

/// <summary>
/// Opens a catalogue file and decides what it is from its content, never from its name alone: a zip (WHO distributes
/// <c>icd102019en.xml.zip</c>; CMS zips the order file), ClaML XML, or the ICD-10-PCS order file (fixed-width text).
/// </summary>
public static partial class CatalogueFile
{
    public const string AcceptedFiles =
        "Upload the WHO ICD-10 ClaML file (icd102019en.xml, or the icd102019en.xml.zip WHO provides) or the CMS ICD-10-PCS order file (icd10pcs_order_YYYY.txt, or its zip).";

    public static CatalogueFileContent Read(Stream content, string fileName, CatalogueFileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanSeek)
        {
            throw new ArgumentException("The catalogue file must be read from a seekable stream.", nameof(content));
        }

        if (content.Length == 0)
        {
            throw new CatalogueFileException("The file is empty.");
        }

        if (content.Length > limits.MaxUploadBytes)
        {
            throw new CatalogueFileException($"The file is larger than {limits.MaxUploadBytes / (1024 * 1024)} MB. {AcceptedFiles}");
        }

        content.Position = 0;
        Span<byte> head = stackalloc byte[4];
        var read = content.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        content.Position = 0;
        return read == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4
            ? ReadZip(content, limits)
            : ReadPlain(content, fileName, null, limits);
    }

    private static CatalogueFileContent ReadZip(Stream content, CatalogueFileLimits limits)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new CatalogueFileException("The zip file is damaged and cannot be opened.", ex);
        }

        using (zip)
        {
            if (zip.Entries.Count > limits.MaxZipEntries)
            {
                throw new CatalogueFileException($"The zip holds {zip.Entries.Count} files. {AcceptedFiles}");
            }

            var candidates = zip.Entries
                .Where(e => e.Name.Length > 0 && !IsMacMetadata(e)
                            && (e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                || PcsOrderFileName().IsMatch(e.Name)))
                .ToList();
            if (candidates.Count != 1)
            {
                var names = string.Join(", ", candidates.Select(e => e.Name).Take(5));
                throw new CatalogueFileException(candidates.Count == 0
                    ? $"The zip holds no ClaML (.xml) or ICD-10-PCS order file. {AcceptedFiles}"
                    : $"The zip holds more than one catalogue file ({names}). Upload the one you want to import on its own.");
            }

            var entry = candidates[0];
            if (entry.Length > limits.MaxUnzippedBytes)
            {
                throw new CatalogueFileException($"{entry.Name} is larger than {limits.MaxUnzippedBytes / (1024 * 1024)} MB once unzipped.");
            }

            try
            {
                using var unzipped = new LimitedReadStream(entry.Open(), limits.MaxUnzippedBytes, entry.Name);
                return ReadPlain(unzipped, entry.Name, entry.Name, limits);
            }
            catch (InvalidDataException ex)
            {
                throw new CatalogueFileException($"{entry.Name} in the zip is damaged and cannot be read.", ex);
            }
        }
    }

    /// <summary>
    /// The extra entries a Mac adds when it zips a folder: <c>__MACOSX/</c> and <c>._name</c> files hold Finder
    /// metadata, not the file itself.
    /// </summary>
    private static bool IsMacMetadata(ZipArchiveEntry entry) =>
        entry.FullName.Replace('\\', '/').Split('/').Contains("__MACOSX", StringComparer.OrdinalIgnoreCase)
        || entry.Name.StartsWith("._", StringComparison.Ordinal);

    /// <summary>
    /// Plain XML or text: decided by the first character that is not a byte-order mark or white space. A ClaML file opens
    /// with <c>&lt;</c>; the order file with a five-digit order number.
    /// </summary>
    private static CatalogueFileContent ReadPlain(Stream content, string fileName, string? innerFileName, CatalogueFileLimits limits)
    {
        // Peek through a buffer so a zip entry's stream, which cannot seek, is read once from the start.
        var buffered = new BufferedStream(content, 64 * 1024);
        var first = FirstSignificantByte(buffered, out var prefix);
        var replay = new PrefixedStream(prefix, buffered);
        if (first == (byte)'<')
        {
            return new CatalogueFileContent(CatalogueKind.Icd10, innerFileName, ClamlReader.Read(replay, limits), null);
        }

        if (first is >= (byte)'0' and <= (byte)'9')
        {
            return new CatalogueFileContent(CatalogueKind.Icd10Pcs, innerFileName, null, PcsOrderFileReader.Read(replay, innerFileName ?? fileName, limits));
        }

        throw new CatalogueFileException($"HC360 cannot read this file. {AcceptedFiles}");
    }

    /// <summary>Reads past a UTF-8 byte-order mark and white space, keeping every byte read so it can be replayed.</summary>
    private static int FirstSignificantByte(Stream stream, out byte[] prefix)
    {
        var seen = new List<byte>();
        while (seen.Count < 4096)
        {
            var value = stream.ReadByte();
            if (value < 0)
            {
                break;
            }

            seen.Add((byte)value);
            var isBom = seen.Count <= 3 && value is 0xEF or 0xBB or 0xBF;
            if (!isBom && value is not (' ' or '\t' or '\r' or '\n'))
            {
                prefix = [.. seen];
                return value;
            }
        }

        prefix = [.. seen];
        return -1;
    }

    [GeneratedRegex(@"^icd10pcs_order_.*\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex PcsOrderFileName();

    /// <summary>Replays bytes already read from a stream that cannot seek, then continues with the stream.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream rest) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_offset < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsSpan(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            return rest.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Stops reading past a limit, whatever the zip's header claimed the size to be.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit, string name) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            _read += read;
            return _read > limit ? throw new CatalogueFileException($"{name} is larger than {limit / (1024 * 1024)} MB once unzipped.") : read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Text helpers shared by the readers.</summary>
internal static partial class CatalogueText
{
    /// <summary>One line of text: runs of white space become one space, and no space is left inside brackets or before punctuation.</summary>
    public static string Clean(string text)
    {
        var collapsed = Whitespace().Replace(text, " ").Trim();
        return SpaceBeforePunctuation().Replace(SpaceAfterOpening().Replace(collapsed, "$1"), "$1");
    }

    /// <summary>Cuts text to a column's length, at a word where it can, marking the cut with an ellipsis.</summary>
    public static string Fit(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text[..(maxLength - 1)];
        var space = cut.LastIndexOf(' ');
        return (space > maxLength / 2 ? cut[..space] : cut).TrimEnd(' ', ',', ';', ':') + "…";
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s+([,;:.)\]])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([(\[])\s+")]
    private static partial Regex SpaceAfterOpening();
}
