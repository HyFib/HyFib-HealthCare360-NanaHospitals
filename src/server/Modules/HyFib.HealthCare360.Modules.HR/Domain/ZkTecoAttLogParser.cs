using System.Globalization;

namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>One attendance record from a terminal, in the terminal's local wall-clock time.</summary>
public sealed record AttLogRecord(string Pin, DateTime LocalTime, int StatusCode, int VerifyMode, string? WorkCode)
{
    public PunchDirection Direction => ZkTecoAttLogParser.DirectionFor(StatusCode);
}

public sealed record AttLogParseResult(IReadOnlyList<AttLogRecord> Records, IReadOnlyList<string> RejectedLines);

/// <summary>
/// Parses the ATTLOG table uploaded by ZKTeco-compatible terminals over the ADMS push protocol:
/// <c>PIN\tYYYY-MM-DD HH:MM:SS\tstatus\tverify\tworkcode\treserved…</c>, one record per line. Some firmware pads with
/// spaces instead of tabs, so whitespace-separated lines are accepted too. Input is untrusted (the protocol has no
/// authentication beyond the serial number), so line count and length are capped and nothing is echoed back.
/// </summary>
public static class ZkTecoAttLogParser
{
    public const int MaxPinLength = 24;
    public const int MaxLineLength = 256;
    public const int MaxLines = 10_000;
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    public static AttLogParseResult Parse(string? body)
    {
        var records = new List<AttLogRecord>();
        var rejected = new List<string>();
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AttLogParseResult(records, rejected);
        }

        var lines = body.Split('\n');
        foreach (var raw in lines.Take(MaxLines))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseLine(line, out var record))
            {
                records.Add(record);
            }
            else
            {
                rejected.Add(line.Length > 64 ? line[..64] : line);
            }
        }

        if (lines.Length > MaxLines)
        {
            rejected.Add($"{lines.Length - MaxLines} line(s) beyond the {MaxLines}-line limit were ignored.");
        }

        return new AttLogParseResult(records, rejected);
    }

    public static bool TryParseLine(string line, out AttLogRecord record)
    {
        record = null!;
        if (line.Length > MaxLineLength)
        {
            return false;
        }

        var fields = line.Split('\t');
        if (fields.Length < 2)
        {
            // Space-padded firmware: "PIN yyyy-MM-dd HH:mm:ss status verify ..." — rejoin date and time.
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 3)
            {
                return false;
            }

            fields = [tokens[0], tokens[1] + " " + tokens[2], .. tokens[3..]];
        }

        var pin = fields[0].Trim();
        if (!IsValidPin(pin))
        {
            return false;
        }

        if (!DateTime.TryParseExact(fields[1].Trim(), TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
        {
            return false;
        }

        var status = fields.Length > 2 && int.TryParse(fields[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 0 and <= 255 ? s : 0;
        var verify = fields.Length > 3 && int.TryParse(fields[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 255 ? v : 0;
        var workCode = fields.Length > 4 ? fields[4].Trim() : null;
        if (string.IsNullOrEmpty(workCode) || workCode == "0" || workCode.Length > 16 || !workCode.All(char.IsAsciiLetterOrDigit))
        {
            workCode = null;
        }

        record = new AttLogRecord(pin, DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified), status, verify, workCode);
        return true;
    }

    /// <summary>Terminal status codes: 0 check-in, 1 check-out, 2 break-out, 3 break-in, 4 overtime-in, 5 overtime-out.</summary>
    public static PunchDirection DirectionFor(int statusCode) => statusCode switch
    {
        0 or 3 or 4 => PunchDirection.In,
        1 or 2 or 5 => PunchDirection.Out,
        _ => PunchDirection.Unknown,
    };

    public static bool IsValidPin(string? pin) =>
        !string.IsNullOrEmpty(pin) && pin.Length <= MaxPinLength && pin.All(char.IsAsciiLetterOrDigit);
}
