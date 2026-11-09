using System.Globalization;
using HyFib.HealthCare360.Platform.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// The server's log events to the syslog collector (<see cref="SyslogClient"/>, RFC 5424 over TLS), as the log file has them:
/// the message with its exception, and the logger, correlation id and level as structured data. The client queues them and
/// returns at once, so a collector that does not answer slows no request. Every text is masked on the way out
/// (<see cref="SyslogFormat"/>, SRS SEC-10).
/// </summary>
internal sealed class SyslogSink(SyslogClient syslog) : ILogEventSink
{
    private static readonly MessageTemplateTextFormatter Text = new("{Message:lj}", CultureInfo.InvariantCulture);

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        syslog.Enqueue(Entry(logEvent));
    }

    internal static SyslogEntry Entry(LogEvent logEvent)
    {
        using var message = new StringWriter(CultureInfo.InvariantCulture);
        Text.Format(logEvent, message);
        if (logEvent.Exception is { } exception)
        {
            message.Write('\n');
            message.Write(exception.ToString());
        }

        return new SyslogEntry(
            logEvent.Timestamp,
            Severity(logEvent.Level),
            null,
            message.ToString(),
            [
                new("logger", Scalar(logEvent, JsonLogFormatter.SourceContext)),
                new("correlationId", Scalar(logEvent, JsonLogFormatter.CorrelationId)),
                new("level", logEvent.Level.ToString()),
            ]);
    }

    internal static SyslogSeverity Severity(LogEventLevel level) => level switch
    {
        LogEventLevel.Fatal => SyslogSeverity.Critical,
        LogEventLevel.Error => SyslogSeverity.Error,
        LogEventLevel.Warning => SyslogSeverity.Warning,
        LogEventLevel.Information => SyslogSeverity.Informational,
        _ => SyslogSeverity.Debug,
    };

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue { Value: { } scalar }
            ? Convert.ToString(scalar, CultureInfo.InvariantCulture)
            : null;
}
