using System.Text.Json;
using Hl7.Fhir.Serialization;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>FHIR JSON serialisation with the Firely SDK (compact by default, indented for <c>_pretty=true</c>).</summary>
internal static class FhirJson
{
    public const string ContentType = "application/fhir+json; charset=utf-8";

    private static readonly JsonSerializerOptions Compact = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector);
    private static readonly JsonSerializerOptions Indented = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector).Pretty();

    public static JsonSerializerOptions Options(bool pretty) => pretty ? Indented : Compact;

    public static string Serialize(R4.Resource resource, bool pretty = false) => JsonSerializer.Serialize(resource, typeof(R4.Resource), Options(pretty));
}

/// <summary>
/// Writes a FHIR resource as <c>application/fhir+json</c>. Responses carry PHI, so they are never stored by caches
/// (<c>Cache-Control: no-store</c>); only the CapabilityStatement may be revalidated.
/// </summary>
internal sealed class FhirResult(R4.Resource resource, int statusCode, bool pretty = false, string cacheControl = "no-store") : IResult
{
    public R4.Resource Resource => resource;

    public int StatusCode => statusCode;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        response.StatusCode = statusCode;
        response.ContentType = FhirJson.ContentType;
        response.Headers.CacheControl = cacheControl;
        await JsonSerializer.SerializeAsync(response.Body, resource, typeof(R4.Resource), FhirJson.Options(pretty), httpContext.RequestAborted);
    }
}
