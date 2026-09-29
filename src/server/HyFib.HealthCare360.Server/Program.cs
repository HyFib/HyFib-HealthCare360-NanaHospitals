using System.IO.Compression;
using System.Security.Authentication;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

// ---------------------------------------------------------------------------------------------------------
// Maintenance commands (run from an elevated prompt on the Server Node), e.g.
//   HyFib.HealthCare360.Server.exe backup decrypt --in <file.hc360bak> --out <file.dump> --key <base64>
// ---------------------------------------------------------------------------------------------------------
if (args.Length > 0 && ServerCli.IsCommand(args[0]))
{
    return await ServerCli.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Site-specific settings live beside the data (survive upgrades): %ProgramData%\HyFib\HealthCare360\config\hc360.settings.json
var platformOptions = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
var directories = new DataDirectories(platformOptions);
directories.EnsureCreated();
builder.Configuration.AddJsonFile(Path.Combine(directories.Root, "config", "hc360.settings.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("HC360_");
platformOptions = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? platformOptions;

builder.Services.AddWindowsService(options => options.ServiceName = "HyFib HealthCare360");

builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "HC360.Server")
        .WriteTo.Console(restrictedToMinimumLevel: builder.Environment.IsDevelopment() ? LogEventLevel.Debug : LogEventLevel.Information)
        .WriteTo.File(
            Path.Combine(directories.Logs, "hc360-server-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 60,
            fileSizeLimitBytes: 100 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {CorrelationId} {Message:lj}{NewLine}{Exception}");

    if (OperatingSystem.IsWindows() && !builder.Environment.IsDevelopment())
    {
        logger.WriteTo.EventLog("HyFib HealthCare360", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Error);
    }
});

// TLS for the hospital LAN/WiFi: generated hospital CA + server certificate (or an admin-supplied PFX).
var bootstrapProtection = HospitalDataProtection.CreateProvider(directories);
using var bootstrapLoggers = LoggerFactory.Create(logging => logging.AddSimpleConsole());
var certificates = new LanCertificateManager(directories, platformOptions, bootstrapProtection, bootstrapLoggers.CreateLogger<LanCertificateManager>());
certificates.EnsureCertificates();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 30 * 1024 * 1024;
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    kestrel.ListenAnyIP(platformOptions.HttpsPort, listen => listen.UseHttps(https =>
    {
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        https.ServerCertificateSelector = (_, _) => certificates.ServerCertificate;
    }));
    if (platformOptions.HttpRedirectPort > 0)
    {
        kestrel.ListenAnyIP(platformOptions.HttpRedirectPort);
    }
});

builder.Services.AddHospitalPlatform(
    builder.Configuration,
    builder.Environment,
    ServerModules.All,
    typeof(ServerModules).Assembly.GetName().Name!,
    certificates);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
});

var signalR = builder.Services.AddSignalR()
    .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
if (builder.Configuration.GetConnectionString("Redis") is { Length: > 0 } redis)
{
    // Scale-out (SCAL-02): several API nodes share real-time groups through Redis.
    signalR.AddStackExchangeRedis(redis, redisOptions => redisOptions.Configuration.ChannelPrefix = StackExchange.Redis.RedisChannel.Literal("hc360"));
}

builder.Services.AddResponseCompression(compression =>
{
    compression.EnableForHttps = true;
    compression.Providers.Add<BrotliCompressionProvider>();
    compression.Providers.Add<GzipCompressionProvider>();
    compression.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json", "application/fhir+json", "image/svg+xml"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.Services.AddOpenApi(openApi =>
{
    openApi.CreateSchemaReferenceId = OpenApiSchemaIds.Create;
    openApi.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "HyFib HealthCare360 API";
        document.Info.Version = "v1";
        document.Info.Description = "Hospital information system API. All endpoints require a bearer token unless marked anonymous.";
        return Task.CompletedTask;
    });
});

if (builder.Configuration["OpenTelemetry:OtlpEndpoint"] is { Length: > 0 } otlp)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation(aspNet => aspNet.EnrichWithHttpResponse = static (activity, response) =>
            {
                // Patient link tokens travel in the path: those requests export their route template instead.
                var path = LoggedPath.Of(response.HttpContext);
                if (!string.Equals(path, response.HttpContext.Request.Path.Value, StringComparison.Ordinal))
                {
                    activity.SetTag("url.path", path);
                }
            })
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter(o => o.Endpoint = new Uri(otlp)))
        .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddMeter("Npgsql").AddOtlpExporter(o => o.Endpoint = new Uri(otlp)));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

if (platformOptions.HttpRedirectPort > 0)
{
    app.UseHttpsRedirection();
}

// Before static files, so the staff and portal shells and their assets carry the security headers too.
app.UseHospitalSecurityHeaders();

// Never compress token-bearing auth responses (BREACH); everything else is compressed to save WiFi airtime.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth") && context.Features.Get<IHttpsCompressionFeature>() is { } compression)
    {
        compression.Mode = HttpsCompressionMode.DoNotCompress;
    }

    return next(context);
});
app.UseResponseCompression();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = static context =>
    {
        // Vite emits content-hashed assets: cache them for a year; always revalidate HTML.
        var headers = context.Context.Response.Headers;
        headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets") || context.Context.Request.Path.StartsWithSegments("/portal/assets")
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    },
});

app.UseSerilogRequestLogging(logging =>
{
    logging.GetLevel = (http, _, exception) =>
        exception is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
        : LogEventLevel.Information;

    // Patient link tokens travel in the path: those requests are logged with their route template (see LoggedPath).
    logging.GetMessageTemplateProperties = static (http, _, elapsed, status) =>
    [
        new LogEventProperty("RequestMethod", new ScalarValue(http.Request.Method)),
        new LogEventProperty("RequestPath", new ScalarValue(LoggedPath.Of(http))),
        new LogEventProperty("StatusCode", new ScalarValue(status)),
        new LogEventProperty("Elapsed", new ScalarValue(elapsed)),
    ];
});

app.UseHospitalPlatform();
app.MapHospitalPlatform();

app.MapHealthChecks("/health/live", new() { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

if (app.Environment.IsDevelopment() || platformOptions.EnableApiReference)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference("/docs", scalar => scalar.WithTitle("HC360 API")).AllowAnonymous();
}

// Unknown API routes must be a 404, never the SPA shell.
app.Map("/api/{**rest}", () => Results.Problem(title: "Not found", statusCode: StatusCodes.Status404NotFound)).AllowAnonymous();
// Portal pages opened from SMS links carry the patient's token in the path (/portal/feedback/{token}). The page is sent
// without rewriting the path, so this endpoint stays current and logs and traces record its route template.
app.MapFallback("/portal/{**path}", async context =>
{
    if (!await TrySendAppPageAsync(context, app.Environment, "portal/index.html"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}).AllowAnonymous().WithSensitivePath();
app.MapFallback(async context =>
{
    if (await TrySendAppPageAsync(context, app.Environment, "index.html"))
    {
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync("""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>HC360 Server</title></head>
        <body style="font-family:Segoe UI,system-ui,sans-serif;padding:2rem">
        <h1>HyFib HealthCare360 server is running</h1>
        <p>The staff UI is not bundled in this build. In development run <code>pnpm dev</code> in <code>src/web</code>.</p>
        </body></html>
        """);
}).AllowAnonymous();

await InitializeDatabaseAsync(app);
await app.RunAsync();
return 0;

// The single-page apps' shell (staff app or portal), never cached, so a new release is picked up at once. It is read
// through the web root's file provider, as static files are (in development that includes the project's wwwroot).
static async Task<bool> TrySendAppPageAsync(HttpContext context, IWebHostEnvironment environment, string page)
{
    var file = environment.WebRootFileProvider.GetFileInfo(page);
    if (!file.Exists || file.IsDirectory)
    {
        return false;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    await context.Response.SendFileAsync(file);
    return true;
}

// PostgreSQL may still be starting when the service boots after a power cut: retry for up to ~3 minutes. Only
// connection problems are retried. An error PostgreSQL reports about the schema or the data (for example a table that
// already exists) happens again on every attempt, so it stops start-up at once with its explanation.
static async Task InitializeDatabaseAsync(WebApplication app)
{
    var initializer = app.Services.GetRequiredService<DatabaseInitializer>();
    var logger = app.Services.GetRequiredService<ILogger<DatabaseInitializer>>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await initializer.InitializeAsync(CancellationToken.None);
            return;
        }
        catch (Exception ex) when (attempt < 12 && IsTransientDatabaseError(ex))
        {
            logger.LogWarning(ex, "Database not ready (attempt {Attempt}); retrying in 15 seconds", attempt);
            await Task.Delay(TimeSpan.FromSeconds(15));
        }
        catch (DatabaseUpgradeException ex)
        {
            logger.LogCritical(ex, "HC360 cannot start: {Reason}", ex.Message);
            throw;
        }
    }
}

static bool IsTransientDatabaseError(Exception ex) => ex switch
{
    TimeoutException => true,
    NpgsqlException npgsql => npgsql.IsTransient,
    InvalidOperationException { InnerException: NpgsqlException inner } => inner.IsTransient,
    _ => false,
};
