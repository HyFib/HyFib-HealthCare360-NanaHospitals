using HyFib.HealthCare360.Platform;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
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

// --role all|api|worker sets HealthCare360:Role (SCAL-05).
args = ServerArguments.Map(args);

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
// The command line again, last, so a switch such as --role wins over the site settings file, as it would over appsettings.
builder.Configuration.AddCommandLine(args);
platformOptions = builder.Configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? platformOptions;

// Again, now that the site settings are in: they may put the keys and documents in a folder shared by several nodes.
directories = new DataDirectories(platformOptions);
directories.EnsureCreated();

builder.Services.AddWindowsService(options => options.ServiceName = "HyFib HealthCare360");

// The database is brought up to date once the service has reported in to Windows, before the web server and the jobs start.
builder.Services.AddDatabaseStartup();

// Every sink masks phone, Aadhaar and ABHA numbers, e-mail addresses and secrets, including exception text; the file is
// structured, one JSON event per line (HospitalLogging).
builder.Services.AddSerilog((services, logger) =>
{
    logger.ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "HC360.Server")
        .WriteToHospitalSinks(
            directories.Logs,
            builder.Environment.IsDevelopment(),
            OperatingSystem.IsWindows() && !builder.Environment.IsDevelopment() ? HospitalLogging.EventLog : null);
});

// TLS for the hospital LAN/WiFi: generated hospital CA + server certificate (or an admin-supplied PFX).
// The node's own key ring: with several nodes, each keeps its own CA copy and certificate (SCAL-01).
var bootstrapProtection = HospitalDataProtection.CreateNodeProvider(directories);
using var bootstrapLoggers = LoggerFactory.Create(logging => logging.AddSimpleConsole());
var certificates = new LanCertificateManager(directories, platformOptions, bootstrapProtection, bootstrapLoggers.CreateLogger<LanCertificateManager>());
certificates.EnsureCertificates();

// The staff app, the patient portal and the API reference each have an address of their own (ASVS V3.5.4, ListenerPaths).
var apiReferenceOn = Listeners.ApiReferenceOn(platformOptions, builder.Environment);
if (Listeners.PortProblem(platformOptions, apiReferenceOn) is { } portProblem)
{
    throw new InvalidOperationException($"HC360 cannot start: {portProblem}");
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 30 * 1024 * 1024;
    kestrel.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    void Https(ListenOptions listen) => listen.UseHttps(https => HospitalHttps.Configure(https, certificates));

    kestrel.ListenAnyIP(platformOptions.HttpsPort, Https);
    kestrel.ListenAnyIP(platformOptions.PortalHttpsPort, Https);
    if (apiReferenceOn)
    {
        // The server machine only: the reference shows the whole API, and IT turns it on for integration work.
        kestrel.ListenLocalhost(platformOptions.ApiReferencePort, Https);
    }

    if (platformOptions.HttpRedirectPort > 0)
    {
        kestrel.ListenAnyIP(platformOptions.HttpRedirectPort);
    }
});

// Plain HTTP goes to the staff app's address: with more than one HTTPS address, the port cannot be inferred.
builder.Services.AddHttpsRedirection(redirection => redirection.HttpsPort = platformOptions.HttpsPort);

builder.AddServerApplication(certificates);

builder.Services.AddHospitalCompression();

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

builder.Services.AddHospitalTelemetry(builder.Configuration);

var app = builder.Build();

// Before anything looks at the client's address or the scheme: behind a reverse proxy (SCAL-01), the client a named proxy
// forwarded for. Nothing changes until HealthCare360:Network:KnownProxies or KnownNetworks name one.
app.UseHospitalForwardedHeaders(platformOptions.Network);

// First, so the request log and the exception handler's entries carry the request's correlation id (OBS-04).
app.UseCorrelationId();

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

// Each address serves only its own app (ListenerPaths), the app shells and their assets included.
app.UseListenerGate();

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

// A worker node (--role worker) serves no app: only its health checks (UseServerApplication).
if (platformOptions.ServesApi)
{
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
}

app.UseSerilogRequestLogging(logging =>
{
    logging.GetLevel = (http, _, exception) =>
        exception is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
        : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
        : LogEventLevel.Information;

    // The request log is written by Serilog directly, outside the logging scope the correlation middleware opens.
    logging.EnrichDiagnosticContext = static (diagnostics, http) => diagnostics.Set("CorrelationId", http.TraceIdentifier);

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

// The reference and its schema answer only on their own address (ListenerPaths), which listens on the server machine
// alone: the API's shape is not published to the hospital network, and the reference's script shares no origin, and no
// cookie, with the staff app.
if (apiReferenceOn && platformOptions.ServesApi)
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference("/docs", scalar => scalar.WithTitle("HC360 API")).AllowAnonymous();
}

// Portal pages opened from SMS links carry the patient's token in the path (/portal/feedback/{token}). The page is sent
// without rewriting the path, so this endpoint stays current and logs and traces record its route template.
if (platformOptions.ServesApi)
{
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
}

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
