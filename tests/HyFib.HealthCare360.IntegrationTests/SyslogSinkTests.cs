using System.Text;
using HyFib.HealthCare360.Platform.Logging;
using HyFib.HealthCare360.Server;
using Serilog.Events;
using Serilog.Parsing;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The server's log events as the syslog collector gets them (HospitalLogging): RFC 5424, with the level as its severity
/// and the logger and correlation id as structured data, and nothing personal in the message, its values or its exception
/// (SRS SEC-10).
/// </summary>
public sealed class SyslogSinkTests
{
    [Fact]
    public void A_log_event_goes_masked_with_its_logger_and_correlation_id_as_structured_data()
    {
        var logEvent = new LogEvent(
            new DateTimeOffset(2026, 11, 9, 4, 45, 30, TimeSpan.Zero),
            LogEventLevel.Warning,
            new InvalidOperationException("The 'To' number +919876543210 is not a valid phone number."),
            new MessageTemplateParser().Parse("Notification {TemplateKey} to {Email} failed: {Error}"),
            [
                new LogEventProperty("TemplateKey", new ScalarValue("auth.otp")),
                new LogEventProperty("Email", new ScalarValue("asha.devi@example.com")),
                new LogEventProperty("Error", new ScalarValue("Unable to deliver to 98765 43210, Aadhaar 2345 6789 0123")),
                new LogEventProperty(JsonLogFormatter.SourceContext, new ScalarValue("HyFib.HealthCare360.Platform.Notifications.NotificationService")),
                new LogEventProperty(JsonLogFormatter.CorrelationId, new ScalarValue("0HN7-correlation")),
            ]);

        var entry = SyslogSink.Entry(logEvent);
        var text = Encoding.UTF8.GetString(SyslogFormat.Message(entry, 16, "hc360-srv", "hc360", "1"));

        text.ShouldStartWith("<132>1 2026-11-09T04:45:30.000000Z hc360-srv hc360 1 - ", customMessage: "local0 * 8 + warning (4)");
        text.ShouldContain("[hc360@32473 logger=\"HyFib.HealthCare360.Platform.Notifications.NotificationService\" correlationId=\"0HN7-correlation\" level=\"Warning\"]");
        text.ShouldContain("auth.otp");
        text.ShouldContain("InvalidOperationException", customMessage: "the exception goes with the message, as in the log file");
        foreach (var raw in new[] { "9876543210", "98765 43210", "asha.devi@", "2345 6789 0123" })
        {
            text.ShouldNotContain(raw);
        }
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, SyslogSeverity.Debug)]
    [InlineData(LogEventLevel.Debug, SyslogSeverity.Debug)]
    [InlineData(LogEventLevel.Information, SyslogSeverity.Informational)]
    [InlineData(LogEventLevel.Warning, SyslogSeverity.Warning)]
    [InlineData(LogEventLevel.Error, SyslogSeverity.Error)]
    [InlineData(LogEventLevel.Fatal, SyslogSeverity.Critical)]
    public void Each_level_has_its_syslog_severity(LogEventLevel level, SyslogSeverity severity) =>
        SyslogSink.Severity(level).ShouldBe(severity);

    [Theory]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("error", LogEventLevel.Error)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData(null, LogEventLevel.Information)]
    public void The_least_severe_level_sent_is_read_in_Serilog_or_Microsoft_words(string? name, LogEventLevel level) =>
        HospitalLogging.Level(name).ShouldBe(level);
}
