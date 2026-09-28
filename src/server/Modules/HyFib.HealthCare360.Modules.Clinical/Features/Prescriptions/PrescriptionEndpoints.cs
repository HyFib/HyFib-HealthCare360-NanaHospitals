using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;

/// <summary>/api/v1/clinical/prescriptions — e-prescribing with safety checks, signing, refills and printing (ERX-01..06).</summary>
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
            .Select(p => new { p.Id, p.PrescriptionNumber, p.PatientId, p.Status, p.IsInpatient, p.PrescribedAt, p.SignedAt, p.PractitionerId,
                Drugs = p.Items.OrderBy(i => i.Sequence).Select(i => i.DrugName).ToList() })
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

    /// <summary>Print/share model (ERX-04). Only signed prescriptions are printed; each print is recorded in the audit trail.</summary>
    private static async Task<IResult> Print(Guid id, PrescriptionService prescriptions, ClinicalAccess access, HospitalDbContext db, TimeProvider time,
        CancellationToken ct)
    {
        var loaded = await prescriptions.LoadAsync(id, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!.ToProblem();
        }

        var p = loaded.Value;
        if (p.Status != PrescriptionStatus.Signed)
        {
            return Error.Rule("prescription.not_printable", "Only signed prescriptions can be printed or shared.").ToProblem();
        }

        await access.OpenAsync(p.PatientId, $"Printed prescription {p.PrescriptionNumber}", ct);
        var today = time.Today();
        var patient = await db.Set<Patient>().AsNoTracking().Where(x => x.Id == p.PatientId)
            .Select(x => new { x.Id, x.Uhid, x.FullName, x.DateOfBirth, x.Gender, x.PreferredLanguage }).FirstAsync(ct);
        var diagnoses = p.EncounterId is { } encounterId
            ? await db.Set<Diagnosis>().AsNoTracking().Where(d => d.EncounterId == encounterId && d.Type != DiagnosisType.Differential)
                .OrderByDescending(d => d.IsPrimary).Select(d => d.IcdCode == null ? d.Title : d.Title + " (" + d.IcdCode + ")").ToListAsync(ct)
            : [];
        var allergies = await db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == p.PatientId && a.Status == AllergyStatus.Active)
            .Select(a => a.Allergen).ToListAsync(ct);
        var encounter = p.EncounterId is { } eid
            ? await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(e => e.Id == eid)
                .Select(e => new { e.FollowUpDate, e.FollowUpAdvice }).FirstOrDefaultAsync(ct)
            : null;

        var lines = p.Items.OrderBy(i => i.Sequence).Select(i =>
        {
            var (english, hindi) = FrequencyLabels.For(i.Frequency, i.CustomFrequency);
            return new PrescriptionPrintLine(i.Sequence, i.DrugName, i.GenericName, i.Strength, i.DosageForm, i.Route, i.Dose, i.FrequencyText, english, hindi,
                i.DurationDays, i.Quantity, i.Instructions, i.IsControlledDrug);
        }).ToList();

        return TypedResults.Ok(new PrescriptionPrintModel(
            p.Id, p.PrescriptionNumber, p.Status, p.SignedAt,
            await PrintQueries.LetterheadAsync(db, p.FacilityId, ct),
            await PrintQueries.PrescriberAsync(db, p.PractitionerId, ct),
            new PrescriptionPrintPatient(patient.Id, patient.Uhid, patient.FullName, patient.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
                patient.Gender, patient.PreferredLanguage),
            diagnoses, allergies, lines, p.Advice, encounter?.FollowUpDate, encounter?.FollowUpAdvice, p.RefillsAllowed, p.VerificationToken,
            p.VerificationToken is null ? null : $"/api/v1/public/prescriptions/verify/{p.VerificationToken}"));
    }
}
