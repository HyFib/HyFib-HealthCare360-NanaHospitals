using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;

/// <summary>
/// /api/v1/clinical/discharge-summaries — one summary per admission: draft, prefill, sign, amend and print (the print
/// model, and the A4 document to print and its PDF).
/// </summary>
internal static class DischargeSummaryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/clinical/discharge-summaries").WithTags("Discharge Summaries");

        group.MapGet("/by-admission/{admissionId:guid}", GetByAdmission).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPut("/by-admission/{admissionId:guid}", Save).RequirePermission(ClinicalPermissions.DischargeSummaryWrite)
            .WithValidation<SaveDischargeSummaryRequest>();
        group.MapGet("/by-admission/{admissionId:guid}/prefill", Prefill).RequirePermission(ClinicalPermissions.DischargeSummaryWrite);
        group.MapPost("/{id:guid}/sign", Sign).RequirePermission(ClinicalPermissions.DischargeSummaryWrite);
        group.MapPost("/{id:guid}/amend", Amend).RequirePermission(ClinicalPermissions.DischargeSummaryWrite).WithValidation<AmendDischargeSummaryRequest>();
        group.MapGet("/{id:guid}/print", Print).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/document", Document).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/pdf", Pdf).RequirePermission(ClinicalPermissions.EmrRead);
    }

    private static async Task<IResult> GetByAdmission(Guid admissionId, DischargeSummaryService summaries, ClinicalAccess access, CancellationToken ct)
    {
        var summary = await summaries.Summaries().AsNoTracking().Include(s => s.Amendments).FirstOrDefaultAsync(s => s.AdmissionId == admissionId, ct);
        if (summary is null)
        {
            return Error.NotFound("Discharge summary", admissionId).ToProblem();
        }

        var opened = await access.OpenAsync(summary.PatientId, "Viewed discharge summary", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await summaries.ToDtoAsync(summary, ct));
    }

    private static async Task<IResult> Save(Guid admissionId, SaveDischargeSummaryRequest request, DischargeSummaryService summaries, CancellationToken ct) =>
        (await summaries.SaveDraftAsync(admissionId, request, ct)).ToOk();

    /// <summary>Structured suggestions only (diagnoses, procedures, investigations, medicines); nothing is generated.</summary>
    private static async Task<IResult> Prefill(Guid admissionId, Guid patientId, DischargeSummaryService summaries, ClinicalAccess access, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Prepared discharge summary", ct);
        return opened.IsFailure ? opened.Error!.ToProblem() : TypedResults.Ok(await summaries.PrefillAsync(admissionId, ct));
    }

    private static async Task<IResult> Sign(Guid id, SignDischargeSummaryRequest request, DischargeSummaryService summaries, CancellationToken ct) =>
        (await summaries.SignAsync(id, request.Version, ct)).ToOk();

    private static async Task<IResult> Amend(Guid id, AmendDischargeSummaryRequest request, DischargeSummaryService summaries, CancellationToken ct) =>
        (await summaries.AmendAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Print(Guid id, DischargeSummaryService summaries, ClinicalAccess access, CancellationToken ct)
    {
        var (print, problem) = await OpenAsync(id, "Printed discharge summary", summaries, access, ct);
        return problem ?? TypedResults.Ok(print);
    }

    /// <summary>
    /// The A4 summary as the staff app prints it: the same document as the PDF and the patient's copy. A draft prints
    /// with "DRAFT — not signed" at the top and at the foot of every page.
    /// </summary>
    private static async Task<IResult> Document(Guid id, DischargeSummaryService summaries, ClinicalAccess access, DocumentAssets assets, CancellationToken ct)
    {
        var (print, problem) = await OpenAsync(id, "Printed discharge summary", summaries, access, ct);
        return problem ?? TypedResults.Ok((await DischargeSummaryDocument.BuildAsync(print!, assets, DocumentText.English, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(Guid id, DischargeSummaryService summaries, ClinicalAccess access, DocumentAssets assets, IPdfRenderer renderer,
        CancellationToken ct)
    {
        var (print, problem) = await OpenAsync(id, "Downloaded discharge summary as a PDF", summaries, access, ct);
        return problem ?? await (await DischargeSummaryDocument.BuildAsync(print!, assets, DocumentText.English, ct)).ToPdfAsync(renderer, ct);
    }

    // Every way of printing a summary applies the same rules: the restricted-record guard and an entry in the PHI access
    // audit saying what was done with it.
    private static async Task<(DischargeSummaryPrint? Print, IResult? Problem)> OpenAsync(
        Guid id, string action, DischargeSummaryService summaries, ClinicalAccess access, CancellationToken ct)
    {
        var print = await summaries.PrintAsync(id, ct);
        if (print.IsFailure)
        {
            return (null, print.Error!.ToProblem());
        }

        var opened = await access.OpenAsync(print.Value.Summary.PatientId, action, ct);
        return opened.IsFailure ? (null, opened.Error!.ToProblem()) : (print.Value, null);
    }
}
