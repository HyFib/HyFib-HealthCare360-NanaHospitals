using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Security;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// One log event per line, as a JSON object with the compact log event format's names (<c>@t</c>, <c>@l</c>, <c>@m</c>,
/// <c>@x</c>), so log tools read it without a pattern (SRS OBS-01). It holds what the plain line held: the time, the level,
/// the logger, the correlation id, the message, the exception, and the values the message names, each as a field of its
/// own. A value the message does not name (one pushed onto the log context, say) is not written, as before.
/// <para>
/// Each text is masked by <see cref="SensitiveText"/> before it is encoded, so the masking cannot break the JSON and the
/// JSON escaping cannot hide a phone number from the masking (SRS SEC-10).
/// </para>
/// </summary>
internal sealed class JsonLogFormatter : ITextFormatter
{
    public const string SourceContext = "SourceContext";
    public const string CorrelationId = "CorrelationId";

    private static readonly MessageTemplateTextFormatter Message = new("{Message:lj}", CultureInfo.InvariantCulture);

    // The masked text is JSON-escaped like any other string; only the characters JSON requires are escaped, so a log
    // line reads as written (Devanagari stays Devanagari).
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Options))
        {
            json.WriteStartObject();
            json.WriteString("@t", logEvent.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("@l", logEvent.Level.ToString());
            json.WriteString("@m", SensitiveText.Redact(Render(logEvent)));
            if (logEvent.Exception is { } exception)
            {
                json.WriteString("@x", SensitiveText.Redact(exception.ToString()));
            }

            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in Fields(logEvent))
            {
                if (written.Add(name) && logEvent.Properties.TryGetValue(name, out var value))
                {
                    json.WritePropertyName(name);
                    Write(json, value);
                }
            }

            json.WriteEndObject();
        }

        output.Write(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        output.Write('\n');
    }

    /// <summary>The logger and the correlation id, then the values the message names, in its order.</summary>
    private static IEnumerable<string> Fields(LogEvent logEvent) =>
        new[] { SourceContext, CorrelationId }.Concat(logEvent.MessageTemplate.Tokens.OfType<PropertyToken>().Select(token => token.PropertyName));

    private static string Render(LogEvent logEvent)
    {
        using var message = new StringWriter(CultureInfo.InvariantCulture);
        Message.Format(logEvent, message);
        return message.ToString();
    }

    private static void Write(Utf8JsonWriter json, LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue { Value: null }:
                json.WriteNullValue();
                break;
            case ScalarValue { Value: bool flag }:
                json.WriteBooleanValue(flag);
                break;
            case ScalarValue { Value: DateTimeOffset at }:
                json.WriteStringValue(at.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ScalarValue { Value: DateTime at }:
                json.WriteStringValue(at.ToString("O", CultureInfo.InvariantCulture));
                break;
            case ScalarValue { Value: sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double } number:
                // A number stays a number unless the masking changes it: a mobile or Aadhaar number logged as a number is
                // masked like one logged as text.
                var digits = Convert.ToString(number.Value, CultureInfo.InvariantCulture)!;
                var masked = SensitiveText.Redact(digits);
                if (masked == digits && IsJsonNumber(digits))
                {
                    json.WriteRawValue(digits);
                }
                else
                {
                    json.WriteStringValue(masked);
                }

                break;
            case ScalarValue scalar:
                json.WriteStringValue(SensitiveText.Redact(Convert.ToString(scalar.Value, CultureInfo.InvariantCulture) ?? string.Empty));
                break;
            default:
                // A structure, list or dictionary: written as the message shows it, masked.
                json.WriteStringValue(SensitiveText.Redact(value.ToString(format: "l", CultureInfo.InvariantCulture)));
                break;
        }
    }

    /// <summary>A finite number as JSON writes it (not NaN or infinity, which JSON has no number for).</summary>
    private static bool IsJsonNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);
}
