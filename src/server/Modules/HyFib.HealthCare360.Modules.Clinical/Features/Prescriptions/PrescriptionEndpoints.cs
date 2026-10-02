using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// /api/v1/clinical/prescriptions — e-prescribing with safety checks, signing, refills and printing (ERX-01..06): the
/// print model, and the A5 document to print and its PDF.
/// </summary>
internal static class PrescriptionEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/clinical/prescriptions").WithTags("E-Prescribing");

        group.MapPost("/", Create).RequirePermission(ClinicalPermissions.PrescriptionsWrite).WithValidation<CreatePrescriptionRequest>();
        group.MapGet("/", List).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}", Get).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapPut("/{id:guid}", Update).RequirePermission(ClinicalPermissions.PrescriptionsWrite).WithValidation<UpdatePrescriptionRequest>();
        group.MapPost("/{id:guid}/check", Check).RequirePermission(ClinicalPermissions.PrescriptionsWrite);
        group.MapPost("/{id:guid}/sign", Sign).RequirePermission(ClinicalPermissions.PrescriptionsSign).WithValidation<SignPrescriptionRequest>();
        group.MapPost("/{id:guid}/cancel", Cancel).RequirePermission(ClinicalPermissions.PrescriptionsSign).WithValidation<CancelPrescriptionRequest>();
        group.MapPost("/{id:guid}/repeat", Repeat).RequirePermission(ClinicalPermissions.PrescriptionsWrite);
        group.MapPost("/{id:guid}/refill", Refill).RequirePermission(ClinicalPermissions.PrescriptionsRefill).WithValidation<RefillPrescriptionRequest>();
        group.MapGet("/{id:guid}/print", Print).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/document", Document).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/pdf", Pdf).RequirePermission(ClinicalPermissions.EmrRead);
    }

    private static async Task<IResult> Create(CreatePrescriptionRequest request, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.CreateAsync(request, ct)).ToCreated(p => $"/api/v1/clinical/prescriptions/{p.Id}");

    private static async Task<IResult> List(Guid patientId, [AsParameters] PageQuery query, Guid? encounterId, PrescriptionStatus? status,
        ClinicalAccess access, HospitalDbContext db, CancellationToken ct)
    {
        var opened = await access.OpenAsync(patientId, "Viewed prescriptions", ct);
        if (opened.IsFailure)
        {
            return opened.Error!.ToProblem();
        }

        var rows = db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(p => p.PatientId == patientId);
        if (encounterId is not null)
        {
            rows = rows.Where(p => p.EncounterId == encounterId);
        }

        if (status is not null)
        {
            rows = rows.Where(p => p.Status == status);
        }

        var page = await rows.OrderByDescending(p => p.PrescribedAt)
            .Select(p => new
            {
                p.Id,
                p.PrescriptionNumber,
                p.PatientId,
                p.Status,
                p.IsInpatient,
                p.PrescribedAt,
                p.SignedAt,
                p.PractitionerId,
                Drugs = p.Items.OrderBy(i => i.Sequence).Select(i => i.DrugName).ToList()
            })
            .ToPagedResultAsync(query.ToRequest(), ct);
        var names = await PrintQueries.PractitionerNamesAsync(db, page.Items.Select(p => (Guid?)p.PractitionerId), ct);
        return TypedResults.Ok(page.Map(p => new PrescriptionListItem(p.Id, p.PrescriptionNumber, p.PatientId, p.Status, p.IsInpatient, p.PrescribedAt, p.SignedAt,
            p.PractitionerId, names.GetValueOrDefault(p.PractitionerId), p.Drugs)));
    }

    private static async Task<IResult> Get(Guid id, PrescriptionService prescriptions, ClinicalAccess access, CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!.ToProblem();
        }

        await access.OpenAsync(loaded.Value.PatientId, $"Viewed prescription {loaded.Value.PrescriptionNumber}", ct);
        return TypedResults.Ok(await prescriptions.ToDtoAsync(loaded.Value, ct));
    }

    private static async Task<IResult> Update(Guid id, UpdatePrescriptionRequest request, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.UpdateAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Check(Guid id, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.CheckAsync(id, ct)).ToOk();

    private static async Task<IResult> Sign(Guid id, SignPrescriptionRequest request, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.SignAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Cancel(Guid id, CancelPrescriptionRequest request, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.CancelAsync(id, request.Reason, ct)).ToOk();

    private static async Task<IResult> Repeat(Guid id, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.RepeatAsync(id, ct)).ToCreated(p => $"/api/v1/clinical/prescriptions/{p.Id}");

    private static async Task<IResult> Refill(Guid id, RefillPrescriptionRequest request, PrescriptionService prescriptions, CancellationToken ct) =>
        (await prescriptions.RefillAsync(id, request, ct)).ToCreated(r => $"/api/v1/clinical/prescriptions/{r.Prescription.Id}");

    /// <summary>
    /// Print/share model (ERX-04). Only signed prescriptions are printed; each print is recorded in the audit trail. The
    /// QR code points at the portal's public verification page when the hospital's public address is configured.
    /// </summary>
    private static async Task<IResult> Print(Guid id, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        CancellationToken ct)
    {
        var (model, problem) = await OpenAsync(id, n => $"Printed prescription {n}", prescriptions, builder, access, ct);
        return problem ?? TypedResults.Ok(model);
    }

    /// <summary>The A5 prescription as the staff app prints it: the same document as the PDF and the patient's copy.</summary>
    private static async Task<IResult> Document(Guid id, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        DocumentAssets assets, CancellationToken ct)
    {
        var (model, problem) = await OpenAsync(id, n => $"Printed prescription {n}", prescriptions, builder, access, ct);
        return problem ?? TypedResults.Ok((await PrescriptionDocument.BuildAsync(model!, assets, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(Guid id, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        DocumentAssets assets, IPdfRenderer renderer, CancellationToken ct)
    {
        var (model, problem) = await OpenAsync(id, n => $"Downloaded prescription {n} as a PDF", prescriptions, builder, access, ct);
        return problem ?? await (await PrescriptionDocument.BuildAsync(model!, assets, ct)).ToPdfAsync(renderer, ct);
    }

    // Every way of printing a prescription applies the same rules: the restricted-record guard (in LoadAsync), signed
    // prescriptions only, and an entry in the PHI access audit saying what was done with it.
    private static async Task<(PrescriptionPrintModel? Model, IResult? Problem)> OpenAsync(
        Guid id, Func<string, string> action, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return (null, loaded.Error!.ToProblem());
        }

        var p = loaded.Value;
        if (p.Status != PrescriptionStatus.Signed)
        {
            return (null, Error.Rule("prescription.not_printable", "Only signed prescriptions can be printed or shared.").ToProblem());
        }

        var opened = await access.OpenAsync(p.PatientId, action(p.PrescriptionNumber), ct);
        if (opened.IsFailure)
        {
            return (null, opened.Error!.ToProblem());
        }

        return (await builder.BuildAsync(p, ct), null);
    }
}
