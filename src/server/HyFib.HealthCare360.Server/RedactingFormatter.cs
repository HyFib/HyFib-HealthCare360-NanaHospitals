using System.Globalization;
using HyFib.HealthCare360.Platform.Security;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// Writes each log event through <see cref="SensitiveText"/>: the finished line is masked (message, properties and
/// exception text alike), whatever the code that logged it passed in (SRS SEC-10).
/// </summary>
internal sealed class RedactingFormatter(ITextFormatter inner) : ITextFormatter
{
    public RedactingFormatter(string outputTemplate)
        : this(new MessageTemplateTextFormatter(outputTemplate, CultureInfo.InvariantCulture))
    {
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        using var line = new StringWriter(CultureInfo.InvariantCulture);
        inner.Format(logEvent, line);
        output.Write(SensitiveText.Redact(line.ToString()));
    }
}
