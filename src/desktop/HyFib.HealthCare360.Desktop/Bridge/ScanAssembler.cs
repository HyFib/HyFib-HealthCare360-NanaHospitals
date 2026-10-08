using System.Text;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>One scan from a serial scanner: the barcode's text, or why it could not be read.</summary>
internal readonly record struct SerialScan(string? Text, string? Problem);

/// <summary>
/// Turns what a serial scanner sends into scans. A scan ends at a carriage return, line feed or tab (the suffix
/// scanners add to each code), or when the scanner falls silent (<see cref="Flush"/>), for a scanner set to send none.
/// The separators GS1 and 2D codes carry between fields (GS, RS, EOT) are dropped, as a keyboard scanner drops them,
/// and so are NULs, which some USB serial adapters send as the port opens.
/// Anything else outside printable ASCII means the port's speed or parity does not match the scanner's, so that scan
/// is refused rather than typed into a field.
/// </summary>
internal sealed class ScanAssembler
{
    /// <summary>More than any code HC360 reads holds (labels, UHIDs, medicine packs): anything longer is noise on the line.</summary>
    public const int MaxLength = 512;

    public const string Unreadable =
        "The scanner sent something that is not a barcode. Check that the speed, data bits, parity and stop bits here match the scanner's.";

    public const string TooLong = "The scanner sent more than a barcode holds. Check that the scanner ends each code with Enter (CR).";

    private readonly StringBuilder _buffer = new();
    private bool _overflow;

    /// <summary>Adds what arrived and returns the scans it completed.</summary>
    public IReadOnlyList<SerialScan> Append(string chunk)
    {
        List<SerialScan>? scans = null;
        foreach (var c in chunk)
        {
            if (c is '\r' or '\n' or '\t')
            {
                if (End() is { } scan)
                {
                    (scans ??= []).Add(scan);
                }
            }
            else if (_buffer.Length < MaxLength)
            {
                _buffer.Append(c);
            }
            else
            {
                _overflow = true;
            }
        }

        return scans ?? (IReadOnlyList<SerialScan>)[];
    }

    /// <summary>The scan in progress, now that the scanner has fallen silent; null when there is none.</summary>
    public SerialScan? Flush() => End();

    private SerialScan? End()
    {
        var raw = _buffer.ToString();
        var overflow = _overflow;
        _buffer.Clear();
        _overflow = false;
        if (overflow)
        {
            return new SerialScan(null, TooLong);
        }

        var text = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (c is >= ' ' and <= '~')
            {
                text.Append(c);
            }
            else if (c is not ('\u001D' or '\u001E' or '\u0004' or '\0'))
            {
                return new SerialScan(null, Unreadable);
            }
        }

        var code = text.ToString().Trim();
        return code.Length == 0 ? null : new SerialScan(code, null);
    }
}
