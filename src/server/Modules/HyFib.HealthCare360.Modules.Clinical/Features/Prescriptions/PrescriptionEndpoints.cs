using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>
/// /api/v1/clinical/prescriptions — e-prescribing with safety checks, signing, refills and printing (ERX-01..06): the
/// print model, and the A5 document to print and its PDF; and the doctor's DSC signature (ERX-04): the bytes HC360
/// Desktop signs, the signature it sends back, its check, and the .p7s with the signed bytes for an independent check.
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
        group.MapGet("/{id:guid}/signing-content", SigningContent).RequirePermission(ClinicalPermissions.PrescriptionsSign);
        group.MapPost("/{id:guid}/digital-signature", AttachSignature).RequirePermission(ClinicalPermissions.PrescriptionsSign)
            .WithValidation<AttachDigitalSignatureRequest>();
        group.MapGet("/{id:guid}/digital-signature", SignatureStatus).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/signature.p7s", SignatureFile).RequirePermission(ClinicalPermissions.EmrRead);
        group.MapGet("/{id:guid}/signed-content", SignedContent).RequirePermission(ClinicalPermissions.EmrRead);
    }

    /// <summary>The .p7s of a signature: DER-encoded detached CMS/PKCS#7 SignedData.</summary>
    public const string SignatureContentType = "application/pkcs7-signature";

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
        PrescriptionSignatureSettings signatures, CancellationToken ct)
    {
        var (model, problem) = await OpenAsync(id, n => $"Printed prescription {n}", prescriptions, builder, access, signatures, ct);
        return problem ?? TypedResults.Ok(model);
    }

    /// <summary>The A5 prescription as the staff app prints it: the same document as the PDF and the patient's copy.</summary>
    private static async Task<IResult> Document(Guid id, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        PrescriptionSignatureSettings signatures, DocumentAssets assets, CancellationToken ct)
    {
        var (model, problem) = await OpenAsync(id, n => $"Printed prescription {n}", prescriptions, builder, access, signatures, ct);
        return problem ?? TypedResults.Ok((await PrescriptionDocument.BuildAsync(model!, assets, ct)).ToDto());
    }

    private static async Task<IResult> Pdf(Guid id, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        PrescriptionSignatureSettings signatures, DocumentAssets assets, IPdfRenderer renderer, CancellationToken ct)
    {
        var (model, problem) = await OpenAsync(id, n => $"Downloaded prescription {n} as a PDF", prescriptions, builder, access, signatures, ct);
        return problem ?? await (await PrescriptionDocument.BuildAsync(model!, assets, ct)).ToPdfAsync(renderer, ct);
    }

    /// <summary>The canonical bytes for HC360 Desktop to sign with the doctor's DSC (the prescribing doctor only).</summary>
    private static async Task<IResult> SigningContent(Guid id, PrescriptionDigitalSignatureService signatures, CancellationToken ct) =>
        (await signatures.ContentAsync(id, ct)).ToOk();

    private static async Task<IResult> AttachSignature(Guid id, AttachDigitalSignatureRequest request, PrescriptionDigitalSignatureService signatures,
        CancellationToken ct) =>
        (await signatures.AttachAsync(id, request, ct)).ToOk();

    private static async Task<IResult> SignatureStatus(Guid id, PrescriptionService prescriptions, PrescriptionDigitalSignatureService signatures,
        CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        return loaded.IsFailure ? loaded.Error!.ToProblem() : TypedResults.Ok(await signatures.StatusAsync(loaded.Value, ct));
    }

    /// <summary>The DSC signature as a .p7s (no patient data in it: the signature, the certificate, the signing time).</summary>
    private static async Task<IResult> SignatureFile(Guid id, PrescriptionService prescriptions, CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!.ToProblem();
        }

        return loaded.Value.DigitalSignature is { } signature
            ? TypedResults.File(signature, SignatureContentType, $"prescription-{loaded.Value.PrescriptionNumber}.p7s")
            : Error.NotFound("Digital signature", id).ToProblem();
    }

    /// <summary>
    /// The exact bytes the DSC signed (<see cref="PrescriptionCanonicalForm"/>), to check the .p7s with
    /// <c>openssl cms -verify -binary -inform DER -in prescription.p7s -content prescription.json -noverify</c>. They
    /// name the patient and the medicines, so reading them is recorded in the PHI access audit.
    /// </summary>
    private static async Task<IResult> SignedContent(Guid id, PrescriptionService prescriptions, ClinicalAccess access, CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!.ToProblem();
        }

        var p = loaded.Value;
        if (p.SignedContent is not { } content)
        {
            return Error.NotFound("Digital signature", id).ToProblem();
        }

        var opened = await access.OpenAsync(p.PatientId, $"Downloaded the signed content of prescription {p.PrescriptionNumber}", ct);
        return opened.IsFailure
            ? opened.Error!.ToProblem()
            : TypedResults.File(content, PrescriptionCanonicalForm.ContentType, $"prescription-{p.PrescriptionNumber}.json");
    }

    // Every way of printing a prescription applies the same rules: the restricted-record guard (in LoadAsync), signed
    // prescriptions only (and, where the hospital requires it, signed with the doctor's DSC), and an entry in the PHI
    // access audit saying what was done with it.
    private static async Task<(PrescriptionPrintModel? Model, IResult? Problem)> OpenAsync(
        Guid id, Func<string, string> action, PrescriptionService prescriptions, PrescriptionPrintBuilder builder, ClinicalAccess access,
        PrescriptionSignatureSettings signatures, CancellationToken ct)
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

        if (await signatures.FinalCopyProblemAsync(p, ct) is { } unsigned)
        {
            return (null, unsigned.ToProblem());
        }

        var opened = await access.OpenAsync(p.PatientId, action(p.PrescriptionNumber), ct);
        if (opened.IsFailure)
        {
            return (null, opened.Error!.ToProblem());
        }

        return (await builder.BuildAsync(p, ct), null);
    }
}
