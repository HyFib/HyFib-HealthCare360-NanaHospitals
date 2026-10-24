using System.Diagnostics;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// OpenTelemetry (SRS OBS-02): traces of the API's requests, the calls it makes out and its database commands, and
/// metrics of the same and of the anti-automation check, exported over OTLP to the collector named by
/// <see cref="EndpointKey"/>. The export is optional: without a collector nothing is collected.
/// </summary>
internal static class HospitalTelemetry
{
    public const string EndpointKey = "OpenTelemetry:OtlpEndpoint";

    /// <summary>The meters exported besides ASP.NET Core's and HttpClient's, which their instrumentation adds.</summary>
    public static readonly string[] Meters = ["Npgsql", AntiAutomation.MeterName];

    /// <returns>True when a collector is set and telemetry is collected for it.</returns>
    public static bool AddHospitalTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration[EndpointKey] is not { Length: > 0 } otlp)
        {
            return false;
        }

        var collector = new Uri(otlp);
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(aspNet => aspNet.EnrichWithHttpResponse = WithheldPath)
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter(o => o.Endpoint = collector))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(Meters)
                .AddOtlpExporter(o => o.Endpoint = collector));
        return true;
    }

    /// <summary>Patient link tokens travel in the path: those requests export their route template instead (<see cref="LoggedPath"/>).</summary>
    internal static void WithheldPath(Activity activity, HttpResponse response)
    {
        var path = LoggedPath.Of(response.HttpContext);
        if (!string.Equals(path, response.HttpContext.Request.Path.Value, StringComparison.Ordinal))
        {
            activity.SetTag("url.path", path);
        }
    }
}
