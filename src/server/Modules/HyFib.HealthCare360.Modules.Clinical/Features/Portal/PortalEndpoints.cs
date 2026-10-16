using HyFib.HealthCare360.Modules.Clinical.Documents;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Portal;

/// <summary>
/// Patient Portal (PP-02/05): prescriptions, a health-record summary (allergies, problems, recent encounters without
/// clinicians' free-text notes, immunisations), signed discharge summaries and record requests — only for patients
/// linked to the signed-in mobile number (<see cref="PortalPatientScope"/>). Signed prescriptions and discharge
/// summaries can be downloaded as the PDF of the document the hospital prints. Every read is recorded in the audit trail.
/// </summary>
internal static class PortalEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/portal").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal);

        group.MapGet("/prescriptions", ListPrescriptions);
        group.MapGet("/prescriptions/{id:guid}", GetPrescription);
        group.MapGet("/prescriptions/{id:guid}/pdf", PrescriptionPdf);
        group.MapGet("/health-records", HealthRecord);
        group.MapGet("/discharge-summaries/{id:guid}", GetDischargeSummary);
        group.MapGet("/discharge-summaries/{id:guid}/pdf", DischargeSummaryPdf);
        group.MapPost("/record-requests", RequestRecords).WithValidation<PortalRecordRequest>();
    }

    private static async Task<IResult> ListPrescriptions(Guid? patientId, [AsParameters] PageQuery query, PortalPatientScope scope, HospitalDbContext db,
        IAuditLogger audit, CancellationToken ct)
    {
        var accessible = await scope.AccessiblePatientIdsAsync(ct);
        if (patientId is { } requested && !accessible.Contains(requested))
        {
            return Error.Forbidden("portal.patient_denied", "This record is not linked to your mobile number.").ToProblem();
        }

        var ids = patientId is { } one ? [one] : accessible.ToList();
        var page = await db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(p => ids.Contains(p.PatientId) && p.Status != PrescriptionStatus.Draft)
            .OrderByDescending(p => p.SignedAt)
            .Select(p => new
            {
                p.Id,
                p.PrescriptionNumber,
                p.PatientId,
                p.Status,
                p.SignedAt,
                p.PractitionerId,
                p.RefillsRemaining,
                Medicines = p.Items.OrderBy(i => i.Sequence).Select(i => i.DrugName).ToList()
            })
            .ToPagedResultAsync(query.ToRequest(), ct);
        foreach (var id in page.Items.Select(p => p.PatientId).Distinct())
        {
            await audit.PatientRecordAccessedAsync(id, "Portal: viewed prescriptions", ct);
        }

        var names = await PrintQueries.PractitionerNamesAsync(db, page.Items.Select(p => (Guid?)p.PractitionerId), ct);
        return TypedResults.Ok(page.Map(p => new PortalPrescriptionListItem(p.Id, p.PrescriptionNumber, p.PatientId, p.Status, p.SignedAt,
            names.GetValueOrDefault(p.PractitionerId), p.Medicines, p.RefillsRemaining)));
    }

    private static async Task<IResult> GetPrescription(Guid id, PortalPatientScope scope, HospitalDbContext db, IAuditLogger audit, ISettingsService settings,
        CancellationToken ct)
    {
        var p = await db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.Status != PrescriptionStatus.Draft, ct);
        if (p is null)
        {
            return Error.NotFound(nameof(Prescription), id).ToProblem();
        }

        var allowed = await scope.EnsureAccessAsync(p.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(p.PatientId, $"Portal: viewed prescription {p.PrescriptionNumber}", ct);
        var prescriber = await PrintQueries.PrescriberAsync(db, p.PractitionerId, ct);
        var facility = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Id == p.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var lines = p.Items.OrderBy(i => i.Sequence).Select(i =>
        {
            var (english, hindi) = FrequencyLabels.For(i.Frequency, i.CustomFrequency);
            return new PortalPrescriptionLine(i.DrugName, i.GenericName, i.Strength, i.DosageForm, i.Dose, i.FrequencyText, english, hindi, i.DurationDays,
                i.Quantity, i.Instructions);
        }).ToList();
        var token = p.Status == PrescriptionStatus.Signed ? p.VerificationToken : null;
        var publicAddress = token is null ? null : await PortalAddress.BaseUrlAsync(settings, p.FacilityId, ct);
        return TypedResults.Ok(new PortalPrescriptionDetail(p.Id, p.PrescriptionNumber, p.PatientId, p.Status, p.SignedAt, prescriber?.DisplayName,
            prescriber?.Qualification, prescriber?.RegistrationNumber, facility, p.Advice, lines, p.RefillsRemaining,
            token, PrescriptionVerificationLinks.Url(publicAddress, token)));
    }

    /// <summary>
    /// The prescription as a PDF: the same A5 document the hospital prints. Only a signed prescription is issued as a
    /// document (the print rule); a cancelled one can still be read on the portal but not downloaded.
    /// </summary>
    private static async Task<IResult> PrescriptionPdf(Guid id, PortalPatientScope scope, HospitalDbContext db, IAuditLogger audit,
        PrescriptionPrintBuilder builder, DocumentAssets assets, IPdfRenderer renderer, CancellationToken ct)
    {
        var p = await db.Set<Prescription>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id && x.Status != PrescriptionStatus.Draft, ct);
        if (p is null)
        {
            return Error.NotFound(nameof(Prescription), id).ToProblem();
        }

        var allowed = await scope.EnsureAccessAsync(p.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        if (p.Status != PrescriptionStatus.Signed)
        {
            return Error.Rule("prescription.not_printable", "Only signed prescriptions can be printed or shared.").ToProblem();
        }

        await audit.PatientRecordAccessedAsync(p.PatientId, $"Downloaded prescription {p.PrescriptionNumber} as a PDF (patient portal)", ct);
        var document = await PrescriptionDocument.BuildAsync(await builder.BuildAsync(p, ct), assets, ct);
        return await document.ToPdfAsync(renderer, ct);
    }

    private static async Task<IResult> HealthRecord(Guid patientId, PortalPatientScope scope, HospitalDbContext db, PatientRecordQueries records,
        IAuditLogger audit, CancellationToken ct)
    {
        var allowed = await scope.EnsureAccessAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(patientId, "Portal: viewed health record", ct);
        var allergies = (await db.Set<Allergy>().AsNoTracking().Where(a => a.PatientId == patientId && a.Status == AllergyStatus.Active).ToListAsync(ct))
            .OrderByDescending(a => a.Severity)
            .Select(a => new AllergySummary(a.Id, a.Allergen, a.Type, a.Severity, a.Reaction))
            .ToList();
        var problems = await db.Set<Diagnosis>().AsNoTracking()
            .Where(d => d.PatientId == patientId && d.Type != DiagnosisType.Differential && (d.Status == DiagnosisStatus.Active || d.Status == DiagnosisStatus.Chronic))
            .OrderByDescending(d => d.RecordedAt)
            .Select(d => new ProblemSummary(d.Id, d.IcdCode, d.Title, d.Type, d.Status, d.OnsetDate))
            .ToListAsync(ct);
        var encounters = (await records.RecentEncountersAsync(patientId, 10, ct))
            .Where(e => e.Status != EncounterStatus.InProgress)
            .ToList();
        var encounterIds = encounters.Select(x => x.Id).ToList();
        var followUps = await db.Set<Encounter>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => encounterIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.FollowUpDate, ct);
        var immunizations = await db.Set<Immunization>().AsNoTracking().Where(i => i.PatientId == patientId && !i.IsEnteredInError)
            .OrderByDescending(i => i.AdministeredOn)
            .Select(i => new PortalImmunization(i.VaccineName, i.DoseNumber, i.AdministeredOn, i.NextDueOn))
            .ToListAsync(ct);
        var summaries = await db.Set<DischargeSummary>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.PatientId == patientId && s.Status != DischargeSummaryStatus.Draft)
            .OrderByDescending(s => s.SignedAt)
            .Select(s => new PortalDischargeSummaryItem(s.Id, s.AdmissionId, s.DischargedAt, s.SignedAt, s.Diagnoses))
            .ToListAsync(ct);

        return TypedResults.Ok(new PortalHealthRecord(patientId, allergies, problems,
            encounters.Select(e => new PortalEncounter(e.Id, e.Type, e.StartedAt, e.PractitionerName, e.DepartmentName, e.Diagnoses, followUps.GetValueOrDefault(e.Id)))
                .ToList(),
            immunizations, summaries));
    }

    private static async Task<IResult> GetDischargeSummary(Guid id, PortalPatientScope scope, DischargeSummaryService summaries, IAuditLogger audit,
        CancellationToken ct)
    {
        var (summary, problem) = await OpenDischargeSummaryAsync(id, "Portal: viewed discharge summary", scope, summaries, audit, ct);
        return problem ?? TypedResults.Ok(await summaries.ToDtoAsync(summary!, ct));
    }

    /// <summary>
    /// The signed summary as a PDF: the same A4 document the hospital prints, with its headings in the request's language
    /// (PP-06: the language chosen in the portal, else the record's preference, else Accept-Language, else English).
    /// </summary>
    private static async Task<IResult> DischargeSummaryPdf(Guid id, PortalPatientScope scope, DischargeSummaryService summaries, IAuditLogger audit,
        DocumentAssets assets, IPdfRenderer renderer, HttpContext http, CancellationToken ct)
    {
        var (summary, problem) = await OpenDischargeSummaryAsync(id, "Downloaded discharge summary as a PDF (patient portal)", scope, summaries, audit, ct);
        if (problem is not null)
        {
            return problem;
        }

        var print = await summaries.PrintAsync(summary!.Id, ct);
        // The portal's choice (X-HC360-Language), else the record's preference, else Accept-Language, else English.
        return print.IsFailure
            ? print.Error!.ToProblem()
            : await (await DischargeSummaryDocument.BuildAsync(print.Value, assets, DocumentText.For(http.PatientLanguage()), ct)).ToPdfAsync(renderer, ct);
    }

    // Both ways of opening a discharge summary on the portal apply the same rules: signed (or amended) summaries only,
    // of a patient linked to the signed-in mobile number, and an audit entry saying what was done with it.
    private static async Task<(DischargeSummary? Summary, IResult? Problem)> OpenDischargeSummaryAsync(
        Guid id, string action, PortalPatientScope scope, DischargeSummaryService summaries, IAuditLogger audit, CancellationToken ct)
    {
        var summary = await summaries.Summaries().AsNoTracking().Include(s => s.Amendments)
            .FirstOrDefaultAsync(s => s.Id == id && s.Status != DischargeSummaryStatus.Draft, ct);
        if (summary is null)
        {
            return (null, Error.NotFound("Discharge summary", id).ToProblem());
        }

        var allowed = await scope.EnsureAccessAsync(summary.PatientId, ct);
        if (allowed.IsFailure)
        {
            return (null, allowed.Error!.ToProblem());
        }

        await audit.PatientRecordAccessedAsync(summary.PatientId, action, ct);
        return (summary, null);
    }

    private static async Task<IResult> RequestRecords(PortalRecordRequest request, PortalPatientScope scope, HospitalDbContext db, MedicalRecordService charts,
        ICurrentUser currentUser, CancellationToken ct)
    {
        var allowed = await scope.EnsureAccessAsync(request.PatientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == request.PatientId)
            .Select(p => new { p.FullName, p.RegisteredFacilityId }).FirstAsync(ct);

        // A family member who sees the patient through a family link asks in the account's name, so the medical records
        // department knows the request is not from the patient's own number.
        var fromOwnNumber = (await scope.OwnNumberPatientIdsAsync(ct)).Contains(request.PatientId);
        var created = await charts.CreateRequestAsync(
            new CreateRecordRequestRequest(request.PatientId, null,
                fromOwnNumber ? patient.FullName : currentUser.DisplayName ?? patient.FullName,
                fromOwnNumber ? "Patient portal" : "Patient portal, family member", request.Purpose, request.Details),
            isPatientRequest: true, facilityId: patient.RegisteredFacilityId, ct);
        return created.IsFailure
            ? created.Error!.ToProblem()
            : TypedResults.Created($"/api/v1/portal/record-requests/{created.Value.Id}", new { created.Value.Id, created.Value.RequestNumber, created.Value.Status });
    }
}
