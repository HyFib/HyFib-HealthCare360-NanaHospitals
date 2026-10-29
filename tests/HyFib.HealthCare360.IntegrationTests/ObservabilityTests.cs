using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS OBS-01 and OBS-02 on the server's own configuration (<see cref="HospitalLogging"/>, <see cref="HospitalTelemetry"/>):
/// the log file holds one structured event per line with patient details masked, only errors reach the Event Log, and
/// with a collector set the API's requests, outgoing calls, database commands and the anti-automation check are traced
/// and measured.
/// </summary>
public sealed class ObservabilityTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "hc360-logs-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_log_file_holds_one_json_event_per_line_with_patient_details_masked()
    {
        var errors = new Collected();
        using (var logger = new LoggerConfiguration().Enrich.FromLogContext()
                   .WriteToHospitalSinks(_logs, development: false, (sink, formatter) => sink.Sink(errors.With(formatter))).CreateLogger())
        {
            var log = logger.ForContext<ObservabilityTests>();
            using (LogContext.PushProperty(JsonLogFormatter.CorrelationId, "c0ffee42"))
            using (LogContext.PushProperty("Caller", "password=hunter2"))
            {
                log.Information("Registered patient {PatientNumber} from {Mobile}", "NH-0000417", "9876543210");
                log.Warning("Called back {Number} after {Minutes} minutes", 9876543210L, 4.5);
                log.Error(new InvalidOperationException("Aadhaar 2345 6789 0123 was refused."), "Could not verify\nthe identity of {PatientNumber}", "NH-0000417");
            }
        }

        var file = Directory.GetFiles(_logs, "hc360-server-*.json").ShouldHaveSingleItem();
        var text = File.ReadAllText(file);
        text.ShouldNotContain("9876543210");
        text.ShouldNotContain("2345 6789 0123");
        text.ShouldNotContain("hunter2", customMessage: "A value the message does not name is not written.");

        var lines = File.ReadAllLines(file);
        lines.Length.ShouldBe(3, "A message with a line break is still one event on one line.");
        var events = lines.Select(line => JsonDocument.Parse(line).RootElement).ToList();

        var registered = events[0];
        DateTimeOffset.Parse(registered.GetProperty("@t").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        registered.GetProperty("@l").GetString().ShouldBe("Information");
        registered.GetProperty("@m").GetString().ShouldBe("Registered patient NH-0000417 from ******3210");
        registered.GetProperty("SourceContext").GetString().ShouldBe(typeof(ObservabilityTests).FullName);
        registered.GetProperty("CorrelationId").GetString().ShouldBe("c0ffee42");
        registered.GetProperty("PatientNumber").GetString().ShouldBe("NH-0000417");
        registered.GetProperty("Mobile").GetString().ShouldBe("******3210");
        registered.TryGetProperty("Caller", out _).ShouldBeFalse();

        var called = events[1];
        called.GetProperty("@l").GetString().ShouldBe("Warning");
        called.GetProperty("Number").GetString().ShouldBe("******3210", "A mobile number logged as a number is masked like one logged as text.");
        called.GetProperty("Minutes").GetDouble().ShouldBe(4.5);

        var failed = events[2];
        failed.GetProperty("@l").GetString().ShouldBe("Error");
        failed.GetProperty("@m").GetString().ShouldBe("Could not verify\nthe identity of NH-0000417");
        failed.GetProperty("@x").GetString().ShouldStartWith("System.InvalidOperationException: Aadhaar XXXX-XXXX-0123 was refused.");

        // The Event Log gets the error alone, masked.
        errors.Events.ShouldHaveSingleItem().Level.ShouldBe(LogEventLevel.Error);
        errors.Lines.ShouldHaveSingleItem().ShouldContain("XXXX-XXXX-0123");
        errors.Lines.Single().ShouldNotContain("2345 6789 0123");
    }

    [Fact]
    public void Without_a_collector_nothing_is_collected()
    {
        var services = new ServiceCollection();

        services.AddHospitalTelemetry(new ConfigurationBuilder().Build()).ShouldBeFalse();

        using var provider = services.BuildServiceProvider();
        provider.GetService<TracerProvider>().ShouldBeNull();
        provider.GetService<MeterProvider>().ShouldBeNull();
    }

    [Fact]
    public void With_a_collector_requests_calls_out_database_commands_and_the_anti_automation_check_are_collected()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [HospitalTelemetry.EndpointKey] = "http://127.0.0.1:4317" })
            .Build();

        services.AddHospitalTelemetry(configuration).ShouldBeTrue();

        using var provider = services.BuildServiceProvider();
        using var tracing = provider.GetRequiredService<TracerProvider>();
        using var metrics = provider.GetRequiredService<MeterProvider>();

        // The sources ASP.NET Core, HttpClient and Npgsql trace with are listened to; others are not.
        foreach (var name in new[] { "Microsoft.AspNetCore", "System.Net.Http", "Npgsql" })
        {
            using var source = new ActivitySource(name);
            source.HasListeners().ShouldBeTrue(name);
        }

        using (var unrelated = new ActivitySource("Some.Other.Library"))
        {
            unrelated.HasListeners().ShouldBeFalse();
        }

        using var meter = new Meter(AntiAutomation.MeterName);
        meter.CreateCounter<long>("hc360.test.instrument").Enabled.ShouldBeTrue("The anti-automation check's meter is collected.");
    }

    [Fact]
    public void A_trace_of_a_patient_link_carries_its_route_not_its_token()
    {
        const string Token = "q3W9xLr0bPz7Kd2Nf5Hs8Tv1Yc4Ma6Je";
        var http = new DefaultHttpContext();
        http.Request.Path = $"/api/v1/public/unsubscribe/{Token}";
        http.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask, RoutePatternFactory.Parse("/api/v1/public/unsubscribe/{token}"), 0,
            new EndpointMetadataCollection(SensitivePathMetadata.Instance), "unsubscribe"));
        using var activity = new Activity("request").SetTag("url.path", http.Request.Path.Value);

        HospitalTelemetry.WithheldPath(activity, http.Response);

        activity.GetTagItem("url.path").ShouldBe("/api/v1/public/unsubscribe/{token}");
    }

    // The Digital Personal Data Protection Rules 2025 (r.6, from 13 May 2027) have logs kept a year, beyond the 180 days
    // CERT-In asks for. The file sink applies its retention when it opens the day's file.
    [Fact]
    public void A_log_file_is_kept_a_year()
    {
        Directory.CreateDirectory(_logs);
        string DaysAgo(int days) =>
            Path.Combine(_logs, $"hc360-server-{DateTime.Now.AddDays(-days).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}.json");
        File.WriteAllText(DaysAgo(200), "{}\n");
        File.WriteAllText(DaysAgo(400), "{}\n");

        using (var logger = new LoggerConfiguration().WriteToHospitalSinks(_logs, development: false, errorLog: null).CreateLogger())
        {
            logger.Information("Today's first event opens today's file");
        }

        File.Exists(DaysAgo(200)).ShouldBeTrue("A log 200 days old is kept.");
        File.Exists(DaysAgo(400)).ShouldBeFalse("A log more than a year old is deleted.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_logs))
        {
            Directory.Delete(_logs, recursive: true);
        }
    }

    /// <summary>A sink that keeps the events it gets and the lines it would write with the formatter it was given.</summary>
    private sealed class Collected : ILogEventSink
    {
        private ITextFormatter? _formatter;

        public List<LogEvent> Events { get; } = [];

        public List<string> Lines { get; } = [];

        public Collected With(ITextFormatter formatter)
        {
            _formatter = formatter;
            return this;
        }

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
            using var line = new StringWriter();
            _formatter!.Format(logEvent, line);
            Lines.Add(line.ToString());
        }
    }
}
