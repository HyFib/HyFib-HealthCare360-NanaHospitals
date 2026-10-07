using System.IO.Compression;
using System.Net;
using System.Security.Authentication;
using HyFib.HealthCare360.Platform;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Networking;
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

// Site-specific settings live beside the data (survive upgrades): %ProgramData%\HyFib\HealthCare360\config\hc360.settings.json.
// Its secrets (the database password) are kept protected with DPAPI and opened as the file loads (SRS SEC-07).
var platformOptions = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
var directories = new DataDirectories(platformOptions);
directories.EnsureCreated();
builder.Configuration.AddProtectedJsonFile(ProtectedSettings.PathIn(directories));
builder.Configuration.AddEnvironmentVariables("HC360_");
platformOptions = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? platformOptions;

builder.Services.AddWindowsService(options => options.ServiceName = "HyFib HealthCare360");

// Every sink writes through RedactingFormatter: phone, Aadhaar and ABHA numbers, e-mail addresses and secrets are masked
// in the finished line, including exception text.
builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "HC360.Server")
        .WriteTo.Console(
            new RedactingFormatter("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"),
            restrictedToMinimumLevel: builder.Environment.IsDevelopment() ? LogEventLevel.Debug : LogEventLevel.Information)
        .WriteTo.File(
            new RedactingFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {CorrelationId} {Message:lj}{NewLine}{Exception}"),
            Path.Combine(directories.Logs, "hc360-server-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 60,
            fileSizeLimitBytes: 100 * 1024 * 1024,
            rollOnFileSizeLimit: true);

    if (OperatingSystem.IsWindows() && !builder.Environment.IsDevelopment())
    {
        logger.WriteTo.EventLog(
            new RedactingFormatter("{Message:lj}{NewLine}{Exception}"), "HyFib HealthCare360", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Error);
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
        // TLS 1.2 is the floor on purpose: older Windows versions the server may run on still enable TLS 1.0/1.1 by default.
#pragma warning disable CA5398 // Avoid hardcoded SslProtocols values
        https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
#pragma warning restore CA5398
        https.ServerCertificateSelector = (_, _) => certificates.ServerCertificate;
    }));
    if (platformOptions.HttpRedirectPort > 0)
    {
        kestrel.ListenAnyIP(platformOptions.HttpRedirectPort);
    }
});

builder.AddServerApplication(certificates);

builder.Services.AddResponseCompression(compression =>
{
    compression.EnableForHttps = true;
    compression.Providers.Add<BrotliCompressionProvider>();
    compression.Providers.Add<GzipCompressionProvider>();
    compression.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json", "application/fhir+json", "image/svg+xml"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// A year, and every name under the server's own (there are none by default, so nothing else on the LAN is affected).
builder.Services.AddHsts(hsts =>
{
    hsts.MaxAge = TimeSpan.FromDays(365);
    hsts.IncludeSubDomains = true;
});

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
// The web apps' text files are UTF-8 (Vite's output): saying so leaves the browser nothing to guess.
foreach (var (extension, type) in new[] { (".html", "text/html"), (".js", "text/javascript"), (".css", "text/css"), (".json", "application/json"), (".svg", "image/svg+xml") })
{
    contentTypes.Mappings[extension] = type + "; charset=utf-8";
}
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

app.UseServerApplication();

if (app.Environment.IsDevelopment() || platformOptions.EnableApiReference)
{
    if (!app.Environment.IsDevelopment())
    {
        // Turned on by IT outside development: the reference and its schema answer only on the server machine itself,
        // so the API's shape is not published to the hospital network. (Kestrel faces the network directly: the
        // address is the connection's own.)
        app.Use((context, next) =>
        {
            if ((context.Request.Path.StartsWithSegments("/docs") || context.Request.Path.StartsWithSegments("/openapi"))
                && !IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }

            return next(context);
        });
    }

    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference("/docs", scalar => scalar.WithTitle("HC360 API")).AllowAnonymous();
}

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

await app.InitializeDatabaseAsync();
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
