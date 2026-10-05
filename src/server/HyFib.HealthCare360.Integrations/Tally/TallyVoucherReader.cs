using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>A ledger line of a voucher as Tally holds it.</summary>
internal sealed record TallyLedgerLine(string Ledger, decimal Amount, bool IsDebit);

/// <summary>A voucher as Tally holds it: read back from Tally's export, or the simulator's copy.</summary>
/// <param name="InBooks">False when Tally holds it cancelled, optional or deleted, so it does not count in the books.</param>
internal sealed record TallyVoucherCopy(string? RemoteId, string? VoucherType, string? Number, DateOnly? Date, IReadOnlyList<TallyLedgerLine> Lines, bool InBooks)
{
    public decimal Amount => Lines.Where(l => l.IsDebit).Sum(l => l.Amount);
}

/// <summary>What a read of Tally's vouchers returned; <paramref name="Error"/> is set when Tally answered with a failure.</summary>
internal sealed record TallyVoucherRead(IReadOnlyList<TallyVoucherCopy> Vouchers, string? Error);

/// <summary>
/// Reads <c>VOUCHER</c> elements out of Tally XML: a collection export (<c>ENVELOPE/BODY/DATA/COLLECTION/VOUCHER</c>) or an
/// import envelope (the simulator's copies use the import layout). The document is streamed, so a busy day is never held
/// as one string, and its size is capped. Like <see cref="TallyResponseParser"/>, DTDs are prohibited and the control
/// character references Tally writes are tolerated.
/// <list type="bullet">
/// <item>The remote id is the <c>REMOTEID</c> attribute, else a <c>REMOTEID</c> or <c>GUID</c> element: Tally keeps an
/// imported voucher's REMOTEID as its GUID, and exports it in both places.</item>
/// <item>Ledger lines are <c>ALLLEDGERENTRIES.LIST</c> (accounting voucher view), else <c>LEDGERENTRIES.LIST</c> (invoice
/// view). A line is a debit when <c>ISDEEMEDPOSITIVE</c> is Yes, or, without it, when its amount is negative.</item>
/// <item>A voucher marked <c>ISCANCELLED</c>, <c>ISOPTIONAL</c> or <c>ISDELETED</c> is not in the books.</item>
/// <item>A failed answer (<c>HEADER/STATUS</c> 0, or a <c>LINEERROR</c>) is reported as an error, never as a day without
/// vouchers, so a closed company is not mistaken for missing vouchers.</item>
/// </list>
/// </summary>
internal static class TallyVoucherReader
{
    /// <summary>A day's export larger than this is refused rather than read.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    public const int MaxVouchers = 100_000;

    private static readonly string[] DateFormats = ["yyyyMMdd", "d-MMM-yyyy", "d-MMM-yy", "yyyy-MM-dd", "dd-MM-yyyy"];

    public static async Task<TallyVoucherRead> ReadAsync(Stream xml, CancellationToken cancellationToken, long maxBytes = MaxBytes)
    {
        await using var limited = new LimitedStream(xml, maxBytes);
        using var reader = XmlReader.Create(limited, new XmlReaderSettings
        {
            Async = true,
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });

        var vouchers = new List<TallyVoucherCopy>();
        string? error = null;
        var failed = false;
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.Name is "VOUCHER" or "HEADER" or "LINEERROR")
            {
                var element = (XElement)await XNode.ReadFromAsync(reader, cancellationToken);
                switch (element.Name.LocalName)
                {
                    case "VOUCHER":
                        if (vouchers.Count >= MaxVouchers)
                        {
                            throw new InvalidDataException($"Tally returned more than {MaxVouchers} vouchers for one day.");
                        }

                        vouchers.Add(Parse(element));
                        break;
                    case "HEADER":
                        failed |= element.Element("STATUS")?.Value.Trim() == "0";
                        break;
                    default:
                        error ??= Text(element.Value);
                        break;
                }

                continue;
            }

            if (failed && error is null && reader.NodeType == XmlNodeType.Text && Text(reader.Value) is { } detail)
            {
                // A failed export carries its reason as the body text ("Could not find Company …").
                error = detail;
            }

            if (!await reader.ReadAsync())
            {
                break;
            }
        }

        if (failed || error is not null)
        {
            return new TallyVoucherRead([], error ?? "Tally reported a failure.");
        }

        return new TallyVoucherRead(vouchers, null);
    }

    public static TallyVoucherRead Read(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return ReadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();
    }

    public static TallyVoucherCopy Parse(XElement voucher)
    {
        var remoteId = new[] { (string?)voucher.Attribute("REMOTEID"), voucher.Element("REMOTEID")?.Value, voucher.Element("GUID")?.Value }
            .Select(Text)
            .Where(id => id is not null)
            .ToList();
        var lines = voucher.Elements("ALLLEDGERENTRIES.LIST").ToList();
        if (lines.Count == 0)
        {
            lines = voucher.Elements("LEDGERENTRIES.LIST").ToList();
        }

        return new TallyVoucherCopy(
            remoteId.FirstOrDefault(id => id!.StartsWith(AccountingVoucher.RemoteIdPrefix, StringComparison.OrdinalIgnoreCase)) ?? remoteId.FirstOrDefault(),
            Text(voucher.Element("VOUCHERTYPENAME")?.Value) ?? Text((string?)voucher.Attribute("VCHTYPE")),
            Text(voucher.Element("VOUCHERNUMBER")?.Value),
            ParseDate(voucher.Element("DATE")?.Value),
            lines.Select(Line).OfType<TallyLedgerLine>().ToList(),
            !(IsYes(voucher.Element("ISCANCELLED")) || IsYes(voucher.Element("ISOPTIONAL")) || IsYes(voucher.Element("ISDELETED"))));
    }

    private static TallyLedgerLine? Line(XElement line)
    {
        var ledger = Text(line.Element("LEDGERNAME")?.Value);
        if (ledger is null
            || !decimal.TryParse(line.Element("AMOUNT")?.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            || amount == 0)
        {
            return null;
        }

        var deemedPositive = line.Element("ISDEEMEDPOSITIVE");
        var isDebit = deemedPositive is null ? amount < 0 : IsYes(deemedPositive);
        return new TallyLedgerLine(ledger, Math.Abs(amount), isDebit);
    }

    private static DateOnly? ParseDate(string? value) =>
        Text(value) is { } text && DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static bool IsYes(XElement? element) => element?.Value.Trim() is { } value && (value.Equals("Yes", StringComparison.OrdinalIgnoreCase) || value == "1");

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Reads at most <paramref name="max"/> bytes from the inner stream, then fails.</summary>
    private sealed class LimitedStream(Stream inner, long max) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return _read > max
                ? throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"Tally's answer is larger than {max / 1024} KB, more than HC360 reads for one day."))
                : read;
        }
    }
}
