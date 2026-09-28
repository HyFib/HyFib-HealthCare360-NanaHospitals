using System.Reflection;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Interop.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// HL7 FHIR R4 read API under <c>/fhir/r4</c> (EHR-05): <c>GET metadata</c> (anonymous, rate-limited), <c>GET {type}/{id}</c>,
/// <c>GET {type}?…</c> and <c>POST {type}/_search</c>. JSON only. Every other path or method under <c>/fhir</c> gets an
/// OperationOutcome (404 or 405), never the web app shell.
/// </summary>
internal static class FhirEndpoints
{
    public const string BasePath = "/fhir/r4";

    private const string Tag = "FHIR R4";

    public static void Map(IEndpointRouteBuilder app)
    {
        var fhir = app.MapGroup(BasePath).WithTags(Tag).AddEndpointFilter<FhirExceptionFilter>();

        fhir.MapGet("/metadata", Metadata).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public);
        fhir.MapGet("/{type}", Search).RequirePermission(InteropPermissions.FhirRead);
        fhir.MapPost("/{type}/_search", SearchByPost).RequirePermission(InteropPermissions.FhirRead);
        fhir.MapGet("/{type}/{id}", Read).RequirePermission(InteropPermissions.FhirRead);

        app.Map("/fhir/{**rest}", Unmatched).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public).WithTags(Tag).ExcludeFromDescription();
    }

    /// <summary>True for requests handled by this facade (their authorization failures are OperationOutcomes too).</summary>
    public static bool IsFhirRequest(PathString path) => path.StartsWithSegments("/fhir", StringComparison.OrdinalIgnoreCase);

    public static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}{BasePath}";

    private static IResult Metadata(HttpContext http, TimeProvider time)
    {
        if (NotAcceptable(http) is { } refused)
        {
            return refused;
        }

        var statement = CapabilityStatementBuilder.Build(BaseUrl(http.Request), SoftwareVersion(), time.GetUtcNow());
        return new FhirResult(statement, StatusCodes.Status200OK, Pretty(http.Request.Query), "no-cache");
    }

    private static async Task<IResult> Read(string type, string id, HttpContext http, FhirApi api, CancellationToken ct)
    {
        if (NotAcceptable(http) is { } refused)
        {
            return refused;
        }

        var response = await api.ReadAsync(type, id, ct);
        return new FhirResult(response.Body, response.StatusCode, Pretty(http.Request.Query));
    }

    private static async Task<IResult> Search(string type, HttpContext http, FhirApi api, CancellationToken ct)
    {
        if (!AcceptsJson(http.Request.Headers.Accept))
        {
            return Error(FhirOutcomes.NotAcceptableFormat());
        }

        var response = await api.SearchAsync(type, http.Request.Query, BaseUrl(http.Request), ct);
        return new FhirResult(response.Body, response.StatusCode, response.Pretty);
    }

    /// <summary>
    /// FHIR search by POST with an <c>application/x-www-form-urlencoded</c> body: keeps identifiers such as the ABHA
    /// number out of request lines. Parameters may also be in the URL.
    /// </summary>
    private static async Task<IResult> SearchByPost(string type, HttpContext http, FhirApi api, CancellationToken ct)
    {
        if (!AcceptsJson(http.Request.Headers.Accept))
        {
            return Error(FhirOutcomes.NotAcceptableFormat());
        }

        if (!http.Request.HasFormContentType)
        {
            return Error(FhirOutcomes.Error(StatusCodes.Status415UnsupportedMediaType, Hl7.Fhir.Model.OperationOutcome.IssueType.NotSupported,
                "POST _search takes its parameters as application/x-www-form-urlencoded.", FhirOutcomes.InvalidParameter));
        }

        var form = await http.Request.ReadFormAsync(ct);
        var parameters = http.Request.Query.Concat(form).ToList();
        var response = await api.SearchAsync(type, parameters, BaseUrl(http.Request), ct);
        return new FhirResult(response.Body, response.StatusCode, response.Pretty);
    }

    private static IResult Unmatched(HttpContext http) =>
        HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            ? Error(FhirOutcomes.Error(StatusCodes.Status404NotFound, Hl7.Fhir.Model.OperationOutcome.IssueType.NotSupported,
                $"Not a supported FHIR interaction. This server offers {BasePath}/metadata, {BasePath}/{{type}}/{{id}} and {BasePath}/{{type}}?….",
                FhirOutcomes.NotFound))
            : Error(FhirOutcomes.ReadOnlyServer());

    /// <summary><c>_format</c> and <c>Accept</c> must allow JSON (the only format served).</summary>
    private static IResult? NotAcceptable(HttpContext http)
    {
        var format = http.Request.Query[SearchDefinitions.Format].ToString();
        return (format.Length > 0 && !SearchRequestParser.IsJson(format)) || !AcceptsJson(http.Request.Headers.Accept)
            ? Error(FhirOutcomes.NotAcceptableFormat())
            : null;
    }

    public static bool AcceptsJson(StringValues accept)
    {
        var header = accept.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        foreach (var range in header.Split(','))
        {
            var mediaType = range.Split(';', 2)[0].Trim();
            if (mediaType is "*/*" or "application/*" || SearchRequestParser.IsJson(mediaType)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Pretty(IQueryCollection query) => bool.TryParse(query[SearchDefinitions.Pretty], out var pretty) && pretty;

    private static FhirResult Error(FhirError error) => new(error.Outcome, error.StatusCode);

    private static string SoftwareVersion()
    {
        var version = typeof(InteropModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }
}

/// <summary>
/// Turns an unexpected failure into a FHIR OperationOutcome (500) with the correlation id. The log names the route and
/// the resource type only: no ids, parameters or patient data.
/// </summary>
internal sealed class FhirExceptionFilter(ILogger<FhirExceptionFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            var http = context.HttpContext;
            var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? FhirEndpoints.BasePath;
            logger.LogError(ex, "FHIR {Method} {Route} failed for resource type {ResourceType}", http.Request.Method, route,
                http.Request.RouteValues.TryGetValue("type", out var type) ? FhirOutcomes.Printable(type?.ToString() ?? string.Empty) : "-");
            var error = FhirOutcomes.Unexpected(http.TraceIdentifier);
            return new FhirResult(error.Outcome, error.StatusCode);
        }
    }
}
