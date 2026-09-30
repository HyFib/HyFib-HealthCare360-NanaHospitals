using HyFib.HealthCare360.Server;
using Serilog.Events;
using Serilog.Parsing;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>The server's log lines are masked as they are written, exception text included (SRS SEC-10).</summary>
public sealed class LogRedactionTests
{
    [Fact]
    public void A_log_line_never_carries_the_phone_number_a_provider_quoted()
    {
        var formatter = new RedactingFormatter("{Message:lj}{NewLine}{Exception}");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            new InvalidOperationException("The 'To' number +919876543210 is not a valid phone number."),
            new MessageTemplateParser().Parse("Notification {TemplateKey} failed: {Error}"),
            [
                new LogEventProperty("TemplateKey", new ScalarValue("auth.otp")),
                new LogEventProperty("Error", new ScalarValue("Unable to deliver to 98765 43210")),
            ]);

        using var output = new StringWriter();
        formatter.Format(logEvent, output);

        output.ToString().ShouldNotContain("9876543210");
        output.ToString().ShouldNotContain("98765 43210");
        output.ToString().ShouldContain("auth.otp");
    }
}
