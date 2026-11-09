using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Platform.Logging;

/// <summary>RFC 5424's severities (section 6.2.1), most severe first.</summary>
public enum SyslogSeverity
{
    Emergency = 0,
    Alert = 1,
    Critical = 2,
    Error = 3,
    Warning = 4,
    Notice = 5,
    Informational = 6,
    Debug = 7,
}

/// <summary>
/// One message for the syslog collector (<see cref="SyslogClient"/>), before it is formatted. <see cref="Data"/> is the
/// message's structured data: name and value pairs of the <see cref="SyslogFormat.DataId"/> element.
/// </summary>
/// <param name="Facility">RFC 5424's facility (0–23); null for the collector's configured one.</param>
public sealed record SyslogEntry(
    DateTimeOffset At,
    SyslogSeverity Severity,
    string? MessageId,
    string Message,
    IReadOnlyList<KeyValuePair<string, string?>>? Data = null,
    int? Facility = null);

/// <summary>
/// The syslog protocol's message format (RFC 5424) and its framing over TLS (RFC 5425, octet counting):
/// <c>MSG-LEN SP &lt;PRI&gt;1 TIMESTAMP HOSTNAME APP-NAME PROCID MSGID STRUCTURED-DATA SP BOM MSG</c>.
/// <para>
/// Every text that reaches the collector, the message and every structured data value alike, is masked here
/// (<see cref="SensitiveText"/>), as the log file is (SRS SEC-10): no caller can send a phone, Aadhaar or ABHA number, an
/// e-mail address or a secret off the server, whatever it passes in.
/// </para>
/// </summary>
public static class SyslogFormat
{
    /// <summary>
    /// The structured data element HC360's values go in. RFC 5424 names a private element <c>name@enterprise-number</c>;
    /// 32473 is the number IANA keeps for examples and documentation (RFC 5612), used until HyFib registers its own.
    /// </summary>
    public const string DataId = "hc360@32473";

    /// <summary>"log audit" (RFC 5424 section 6.2.1), the facility the audit chain's anchors go as.</summary>
    public const int LogAuditFacility = 13;

    private const string Nil = "-";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The message as RFC 5424 writes it, without the frame: the PRI, the header, the structured data and the masked text.</summary>
    public static byte[] Message(SyslogEntry entry, int facility, string? hostName, string appName, string? processId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var priority = ((entry.Facility ?? facility) * 8) + (int)entry.Severity;
        var header = new StringBuilder()
            .Append('<').Append(priority.ToString(CultureInfo.InvariantCulture)).Append(">1 ")
            .Append(Timestamp(entry.At)).Append(' ')
            .Append(Field(hostName, 255)).Append(' ')
            .Append(Field(appName, 48)).Append(' ')
            .Append(Field(processId, 128)).Append(' ')
            .Append(Field(entry.MessageId, 32)).Append(' ')
            .Append(StructuredData(entry.Data));

        var text = SensitiveText.Redact(entry.Message ?? string.Empty);
        if (text.Length == 0)
        {
            return Utf8.GetBytes(header.ToString());
        }

        header.Append(' ');
        var head = Utf8.GetBytes(header.ToString());
        var body = Utf8.GetBytes(text);
        var message = new byte[head.Length + Bom.Length + body.Length];
        head.CopyTo(message, 0);
        Bom.CopyTo(message, head.Length);
        body.CopyTo(message, head.Length + Bom.Length);
        return message;
    }

    /// <summary>The message framed for a stream (RFC 5425 section 4.3): its length in bytes, a space, the message.</summary>
    public static byte[] Frame(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var length = Encoding.ASCII.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + " ");
        var frame = new byte[length.Length + message.Length];
        length.CopyTo(frame, 0);
        message.CopyTo(frame, length.Length);
        return frame;
    }

    /// <summary>RFC 3339 in UTC, to the microsecond (RFC 5424 allows six fractional digits at most).</summary>
    internal static string Timestamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A header field: printable US-ASCII without spaces (33–126), at most <paramref name="max"/> characters, and the nil
    /// value "-" when there is nothing left. Anything else is replaced by '_' rather than break the header.
    /// </summary>
    internal static string Field(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Nil;
        }

        var field = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (field.Length == max)
            {
                break;
            }

            field.Append(c is >= '!' and <= '~' ? c : '_');
        }

        return field.ToString();
    }

    /// <summary>The nil value, or one element of <see cref="DataId"/> with each value masked and escaped (RFC 5424 section 6.3.3).</summary>
    internal static string StructuredData(IReadOnlyList<KeyValuePair<string, string?>>? data)
    {
        var values = data?.Where(p => p.Value is not null).ToList();
        if (values is not { Count: > 0 })
        {
            return Nil;
        }

        var element = new StringBuilder("[").Append(DataId);
        foreach (var (name, value) in values)
        {
            element.Append(' ').Append(ParameterName(name)).Append("=\"");
            foreach (var c in SensitiveText.Redact(value!))
            {
                if (c is '"' or '\\' or ']')
                {
                    element.Append('\\');
                }

                element.Append(c);
            }

            element.Append('"');
        }

        return element.Append(']').ToString();
    }

    /// <summary>A PARAM-NAME: 1 to 32 printable ASCII characters other than '=', ' ', ']' and '"'.</summary>
    internal static string ParameterName(string name)
    {
        var field = Field(name, 32);
        return string.Concat(field.Select(c => c is '=' or ']' or '"' ? '_' : c));
    }
}
