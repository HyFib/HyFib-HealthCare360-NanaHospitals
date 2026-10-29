using HyFib.HealthCare360.Modules.Billing.Documents;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tariffs;

/// <summary>
/// /api/v1/public/rate-list — the rate list for anyone to read on the patient portal without signing in: the list the
/// counter prints and displays (Clinical Establishments Rules 2012, r.9(i)), for each facility that publishes it
/// (<see cref="RateListService.PublishSetting"/>, on unless the hospital turns it off). Anonymous and rate-limited
/// ("public" policy), and off while the patient portal is. Requests carry no facility, so the facility is in the path.
/// </summary>
internal static class PublicRateListEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/public/rate-list")
            .WithTags("Billing - Rate list")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Public)
            .RequireFeature(FeatureKeys.PatientPortal);

        group.MapGet("/", Facilities);
        group.MapGet("/{facilityId:guid}", Get);
        group.MapGet("/{facilityId:guid}/pdf", Pdf);
    }

    private static async Task<IResult> Facilities(RateListService rates, CancellationToken ct) =>
        TypedResults.Ok(await rates.PublishedFacilitiesAsync(ct));

    private static async Task<IResult> Get(Guid facilityId, RateListService rates, CancellationToken ct) =>
        await rates.PublishedAsync(facilityId, ct) is { } list
            ? TypedResults.Ok(list)
            : Error.NotFound("Rate list", facilityId).ToProblem();

    private static async Task<IResult> Pdf(
        Guid facilityId, RateListService rates, DocumentAssets assets, PublicRateListPdf pdfs, CancellationToken ct)
    {
        if (await rates.PublishedAsync(facilityId, ct) is not { } list)
        {
            return Error.NotFound("Rate list", facilityId).ToProblem();
        }

        var document = await RateListDocument.BuildAsync(facilityId, list.Rates, list.Language, assets, ct);
        try
        {
            return TypedResults.File(await pdfs.RenderAsync(document, ct), "application/pdf", document.FileName + ".pdf");
        }
        catch (PdfRenderException)
        {
            // The reason is for staff, who see it in System health; a visitor is told only that there is no PDF just now.
            return DocumentResults.PdfUnavailable("The PDF cannot be made just now. The rates on this page are the same.");
        }
    }
}
