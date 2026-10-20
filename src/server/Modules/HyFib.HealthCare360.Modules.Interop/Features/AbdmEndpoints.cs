using System.Text;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// /api/v1/abdm — ABDM health records (EHR-08) for staff to download: a signed discharge summary as an NRCeS FHIR
/// document bundle (<c>application/fhir+json</c>, an attachment). Every endpoint needs <c>abdm.export</c> and answers only
/// while the ABDM switch is on (503 <c>feature.disabled</c> otherwise); the record-sharing consent and restricted-record
/// rules are those of the FHIR API, and every export is audited.
/// </summary>
internal static class AbdmEndpoints
{
    public const string BasePath = "/api/v1/abdm";

    public static void Map(IEndpointRouteBuilder app)
    {
        var abdm = app.MapGroup(BasePath).WithTags("ABDM").RequireFeature(FeatureKeys.Abdm);

        abdm.MapGet("/discharge-summaries/{id:guid}/bundle", DischargeSummary).RequirePermission(InteropPermissions.AbdmExport);
    }

    private static async Task<IResult> DischargeSummary(Guid id, AbdmDocumentService documents, HttpContext http, CancellationToken ct)
    {
        var document = await documents.DischargeSummaryAsync(id, ct);
        return document.IsFailure ? document.Error!.ToProblem() : Download(document.Value, http);
    }

    /// <summary>The document as indented FHIR JSON, to save as a file. It is a patient's record, so no cache may keep it.</summary>
    private static IResult Download(AbdmDocument document, HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        return TypedResults.File(Encoding.UTF8.GetBytes(FhirJson.Serialize(document.Bundle, pretty: true)), FhirJson.ContentType, document.FileName);
    }
}
