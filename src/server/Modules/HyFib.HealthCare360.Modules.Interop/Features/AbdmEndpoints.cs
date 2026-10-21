using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>
/// <c>/api/v1/abdm</c> (SRS EHR-08), only while ABDM health records are switched on (<see cref="FeatureKeys.Abdm"/>):
/// one record as an ABDM FHIR document (<c>application/fhir+json</c>, a file to save, never cached), and the gateway's set-up for
/// administrators. Errors are the API's usual problem details: 404 for a record that does not exist in the caller's
/// facility, 422 <c>abdm.not_exportable</c> for one not signed or released yet, 403 for a restricted record (or
/// <c>abdm.restricted_duplicate</c> for a restricted duplicate whose records it holds), and 503 <c>pdf.unavailable</c>
/// when its printed PDF could not be made just now.
/// </summary>
internal static class AbdmEndpoints
{
    public const string BasePath = "/api/v1/abdm";

    public static void Map(IEndpointRouteBuilder app)
    {
        var abdm = app.MapGroup(BasePath).WithTags("ABDM").RequireFeature(FeatureKeys.Abdm);

        var bundles = abdm.MapGroup("/bundles").RequirePermission(InteropPermissions.AbdmExport);
        bundles.MapGet("/discharge-summaries/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.DischargeSummary, id, pretty, service, ct));
        bundles.MapGet("/lab-reports/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.LabReport, id, pretty, service, ct));
        bundles.MapGet("/op-consultations/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.OpConsultation, id, pretty, service, ct));
        bundles.MapGet("/prescriptions/{id:guid}", (Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct) =>
            ExportAsync(AbdmRecordType.Prescription, id, pretty, service, ct));

        abdm.MapGet("/gateway", async (AbdmGatewayConfiguration gateway, CancellationToken ct) => TypedResults.Ok(await gateway.StatusAsync(ct)))
            .RequirePermission(PlatformPermissions.IntegrationsManage);
    }

    private static async Task<IResult> ExportAsync(AbdmRecordType type, Guid id, bool? pretty, AbdmBundleService service, CancellationToken ct)
    {
        try
        {
            var export = await service.ExportAsync(type, id, ct);
            return export.IsSuccess
                ? new FhirResult(export.Value.Bundle, StatusCodes.Status200OK, pretty == true, fileName: export.Value.FileName)
                : export.Error!.ToProblem();
        }
        catch (PdfRenderException ex)
        {
            return DocumentResults.PdfUnavailable($"{ex.Message} The ABDM record holds the printed copy: try again in a moment.");
        }
    }
}
