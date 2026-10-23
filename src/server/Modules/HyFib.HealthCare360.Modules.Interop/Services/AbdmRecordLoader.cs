using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using ClinicalEncounter = HyFib.HealthCare360.Modules.Clinical.Domain.Encounter;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// The patient and facility of a record, and why it cannot be exported yet (a draft, an unsigned note), which the caller
/// may learn only once they may open the patient's record.
/// </summary>
internal sealed record AbdmSource(Guid PatientId, Guid FacilityId, string? NotExportable = null)
{
    /// <summary>
    /// The duplicate UHIDs merged into <see cref="PatientId"/> whose records the document may hold, each of which the
    /// caller may open (<see cref="AbdmBundleService"/>).
    /// </summary>
    public IReadOnlyList<Guid> Duplicates { get; init; } = [];
}

/// <summary>
/// Reads one record and the rows its ABDM document is made from, with the FHIR read API's queries (so the same rules
/// apply: only signed, released and verified data, in the caller's facility), and hands them to
/// <see cref="AbdmRecords"/>, which keeps what the record held when it was completed. The PDF the hospital prints is
/// attached when this server can make PDFs (<see cref="AbdmPdfSource"/>).
/// </summary>
internal sealed class AbdmRecordLoader(
    HospitalDbContext db,
    IEnumerable<IFhirResourceProvider> providers,
    PatientRecords patients,
    ReferenceDirectoryLoader references,
    IEnumerable<INotificationDocumentSource> documents,
    IPdfRenderer renderer,
    PrescriptionFinalCopies finalCopies)
{
    // The printed PDFs, made by the modules' document sources (the kinds messages name them by).
    private const string PrescriptionPdf = "clinical.prescription";
    private const string DischargeSummaryPdf = "clinical.discharge-summary";
    private const string LabReportPdf = "diagnostics.lab-report";
    private const string RadiologyReportPdf = "diagnostics.radiology-report";

    private EncounterProvider Encounters => Provider<EncounterProvider>();
    private ConditionProvider Conditions => Provider<ConditionProvider>();
    private AllergyIntoleranceProvider Allergies => Provider<AllergyIntoleranceProvider>();
    private ObservationProvider Observations => Provider<ObservationProvider>();
    private MedicationRequestProvider Medications => Provider<MedicationRequestProvider>();
    private DiagnosticReportProvider Reports => Provider<DiagnosticReportProvider>();
    private DocumentReferenceProvider DocumentReferences => Provider<DocumentReferenceProvider>();

    /// <summary>The record's patient and facility, and whether it is complete; a failure only when it does not exist.</summary>
    public async Task<Result<AbdmSource>> LocateAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        switch (type)
        {
            case AbdmRecordType.Prescription:
            {
                var row = await db.Set<Prescription>().AsNoTracking().Where(p => p.Id == id)
                    .Select(p => new { p.PatientId, p.FacilityId, p.Status }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status != PrescriptionStatus.Signed ? "Only a signed prescription that has not been cancelled can be exported." : null);
            }

            case AbdmRecordType.LabReport:
            {
                var row = await db.Set<LabReport>().AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.PatientId, r.FacilityId, r.Status }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status is not (LabReportStatus.Released or LabReportStatus.Amended) ? "Only a released lab report can be exported." : null);
            }

            case AbdmRecordType.RadiologyReport:
            {
                var row = await db.Set<RadiologyReport>().AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.PatientId, r.FacilityId, r.Status }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id) : new AbdmSource(row.PatientId, row.FacilityId, RadiologyReportNotExportable(row.Status));
            }

            case AbdmRecordType.OpConsultation:
            {
                var row = await db.Set<ClinicalEncounter>().AsNoTracking().Where(e => e.Id == id)
                    .Select(e => new { e.PatientId, e.FacilityId, e.Status, e.Type }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status == EncounterStatus.InProgress ? "Sign the consultation note before exporting it."
                        : row.Type == EncounterType.InPatient
                            ? "An in-patient note is not exported on its own: its diagnoses and prescriptions are in the stay's discharge summary."
                        : null);
            }

            default:
            {
                var row = await db.Set<DischargeSummary>().AsNoTracking().Where(s => s.Id == id)
                    .Select(s => new { s.PatientId, s.FacilityId, s.Status }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status == DischargeSummaryStatus.Draft ? "Sign the discharge summary before exporting it." : null);
            }
        }
    }

    /// <summary>The answer for a record that does not exist, or is not exported because its patient was deleted.</summary>
    public static Error NotFound(AbdmRecordType type, Guid id) => Error.NotFound(
        type switch
        {
            AbdmRecordType.Prescription => nameof(Prescription),
            AbdmRecordType.LabReport => nameof(LabReport),
            AbdmRecordType.RadiologyReport => nameof(RadiologyReport),
            AbdmRecordType.OpConsultation => "Encounter",
            _ => nameof(DischargeSummary),
        },
        id);

    /// <summary>
    /// Why a radiology report is not exported: only a released one is the hospital's report. A draft, a report waiting for
    /// sign-off and one signed off but not released are still the radiologists' work.
    /// </summary>
    internal static string? RadiologyReportNotExportable(DiagnosticReportStatus status) =>
        status == DiagnosticReportStatus.Released ? null : "Only a released radiology report can be exported.";

    /// <summary>
    /// The patients under whom records the document would hold are kept, besides the record's own: for a discharge
    /// summary, those of the stay's lab reports it holds (released by its date, <see cref="AbdmAsOf"/>), which the
    /// laboratory keeps under the UHID they were made for (a duplicate merged since, or wrongly another patient's). Which
    /// of them are the same patient, and may be opened, the caller decides: the document holds only the records of
    /// <see cref="AbdmSource.Duplicates"/>.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> OtherPatientsAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        if (type != AbdmRecordType.DischargeSummary
            || await db.Set<DischargeSummary>().AsNoTracking().Where(s => s.Id == id)
                .Select(s => new { s.AmendedAt, s.SignedAt, s.CreatedAt }).FirstOrDefaultAsync(ct) is not { } summary
            || await StayVisitAsync(id, ct) is not { } stayVisit)
        {
            return [];
        }

        var asOf = summary.AmendedAt ?? summary.SignedAt ?? summary.CreatedAt;
        return [.. (await StayReports(stayVisit).ToListAsync(ct)).Where(r => AbdmAsOf.Released(r, asOf)).Select(r => r.PatientId).Distinct()];
    }

    public Task<AbdmDocument> LoadAsync(AbdmRecordType type, Guid id, AbdmSource source, CancellationToken ct) => type switch
    {
        AbdmRecordType.Prescription => PrescriptionAsync(id, source, ct),
        AbdmRecordType.LabReport => LabReportAsync(id, source, ct),
        AbdmRecordType.RadiologyReport => RadiologyReportAsync(id, source, ct),
        AbdmRecordType.OpConsultation => ConsultationAsync(id, source, ct),
        _ => DischargeSummaryAsync(id, source, ct),
    };

    /// <summary>
    /// Why the hospital may not issue this signed prescription, by the DSC rule of its printout and PDF (a doctor's DSC
    /// required and missing, untrusted or broken), with that rule's codes; null when it may.
    /// </summary>
    public async Task<Error?> PrescriptionNotIssuedAsync(Guid id, CancellationToken ct)
    {
        var prescription = await db.Set<Prescription>().AsNoTracking().Include(p => p.Items).FirstAsync(p => p.Id == id, ct);
        return PrescriptionFinalCopies.Problem(await finalCopies.CheckAsync(prescription, ct));
    }

    private async Task<AbdmDocument> PrescriptionAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var header = await db.Set<Prescription>().AsNoTracking().Where(p => p.Id == id)
            .Select(p => new { p.PrescriptionNumber, p.EncounterId, p.VisitId, p.AdmissionId, p.PractitionerId, p.SignedAt, p.PrescribedAt, p.Advice, p.CreatedAt, p.UpdatedAt })
            .FirstAsync(ct);
        var lines = await Medications.SignedLines(i => true, p => p.Id == id).ToListAsync(ct);
        var (visit, encounter) = await EncounterRowsAsync(header.EncounterId, header.VisitId, header.AdmissionId, ct);
        var context = await ContextAsync(source, visit, encounter, [header.PractitionerId], [header.AdmissionId], [], PrescriptionPdf, id, ct);
        return AbdmRecords.Prescription(new PrescriptionData(
            context, id, header.PrescriptionNumber, header.PractitionerId, header.PrescribedAt, header.SignedAt, header.Advice, header.CreatedAt, header.UpdatedAt, lines));
    }

    private async Task<AbdmDocument> LabReportAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var report = (await Reports.CompleteAsync(await Reports.LabReports(r => r.Id == id).ToListAsync(ct), ct)).First();
        var results = await Observations.ReleasedResults(r => r.LabOrderId == report.LabOrderId).ToListAsync(ct);
        var (visit, encounter) = await EncounterRowsAsync(null, report.VisitId, null, ct);
        var context = await ContextAsync(source, visit, encounter, [], [], [], LabReportPdf, id, ct);
        context = context with { ReferralLabs = await ReferralLabsAsync(results, ct) };
        return AbdmRecords.LabReport(new LabReportData(context, report, results));
    }

    private async Task<AbdmDocument> RadiologyReportAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var report = await Reports.RadiologyReports(r => r.Id == id).FirstAsync(ct);
        report = report with { Addenda = (await Reports.AddendumRowsAsync([id], ct)).GetValueOrDefault(id) ?? [] };
        var doctors = await DoctorsAsync([report.ReportedBy, report.VerifiedBy, .. report.Addenda.Select(a => a.AddedBy)], ct);
        var (visit, encounter) = await EncounterRowsAsync(null, report.VisitId, null, ct);

        // The printed report is made from its imaging study, as everything in radiology is.
        var context = await ContextAsync(source, visit, encounter, [.. doctors.Values.Select(p => (Guid?)p)], [], [], RadiologyReportPdf, report.ImagingStudyId, ct);
        return AbdmRecords.RadiologyReport(new RadiologyReportData(context, report, doctors));
    }

    private async Task<AbdmDocument> ConsultationAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var note = await db.Set<ClinicalEncounter>().AsNoTracking().Where(e => e.Id == id)
            .Select(e => new ConsultationNote
            {
                Id = e.Id,
                PractitionerId = e.PractitionerId,
                SignedByPractitionerId = e.SignedByPractitionerId,
                Status = e.Status,
                SignedAt = e.SignedAt,
                AmendedAt = e.AmendedAt,
                ChiefComplaint = e.ChiefComplaint,
                HistoryOfPresentIllness = e.HistoryOfPresentIllness,
                Examination = e.Examination,
                Assessment = e.Assessment,
                Plan = e.Plan,
                FollowUpDate = e.FollowUpDate,
                FollowUpAdvice = e.FollowUpAdvice,
                ReferralDepartmentId = e.ReferralDepartmentId,
                ReferralPractitionerId = e.ReferralPractitionerId,
                ReferralExternal = e.ReferralExternal,
                ReferralReason = e.ReferralReason,
                CreatedAt = e.CreatedAt,
                UpdatedAt = e.UpdatedAt,
            })
            .FirstAsync(ct);
        if (note.Status == EncounterStatus.Amended)
        {
            note = note with
            {
                AmendedByPractitionerId = await AmenderAsync(
                    db.Set<EncounterAmendment>().Where(a => a.EncounterId == id).OrderByDescending(a => a.AmendedAt).Select(a => a.AmendedByUserId), ct),
            };
        }

        // Everything the note may have held; AbdmRecords keeps what it held when it was signed or amended.
        var diagnoses = await Conditions.Diagnoses(d => d.EncounterId == id).ToListAsync(ct);
        var vitals = await Observations.Vitals(v => v.EncounterId == id && !v.IsEnteredInError).ToListAsync(ct);
        var lines = await Medications.SignedLines(i => true, p => p.EncounterId == id).ToListAsync(ct);
        var allergies = await Allergies.Allergies(a => a.PatientId == source.PatientId && a.Status != AllergyStatus.EnteredInError).ToListAsync(ct);
        var (visit, encounter) = await EncounterRowsAsync(id, null, null, ct);
        var context = await ContextAsync(source, visit, encounter,
            [note.PractitionerId, note.SignedByPractitionerId, note.AmendedByPractitionerId, note.ReferralPractitionerId, .. lines.Select(l => (Guid?)l.PractitionerId)],
            [.. lines.Select(l => l.AdmissionId)], [note.ReferralDepartmentId], null, id, ct);
        return AbdmRecords.Consultation(new ConsultationData(context, note, diagnoses, vitals, lines, allergies));
    }

    private async Task<AbdmDocument> DischargeSummaryAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var summary = await DocumentReferences.SignedSummaries(s => s.Id == id).FirstAsync(ct);
        var stayVisitId = await StayVisitAsync(id, ct);
        var encounterIds = await db.Set<ClinicalEncounter>().AsNoTracking().Where(e => e.AdmissionId == summary.AdmissionId).Select(e => e.Id).ToListAsync(ct);
        if (summary.EncounterId is { } summaryEncounter)
        {
            encounterIds.Add(summaryEncounter);
        }

        // Everything the summary may have held; AbdmRecords keeps what it held when it was signed or amended.
        var diagnoses = await Conditions.Diagnoses(d => d.EncounterId != null && encounterIds.Contains(d.EncounterId.Value)).ToListAsync(ct);
        var lines = await Medications.SignedLines(i => true,
                p => p.AdmissionId == summary.AdmissionId || (p.EncounterId != null && encounterIds.Contains(p.EncounterId.Value)))
            .ToListAsync(ct);
        var allergies = await Allergies.Allergies(a => a.PatientId == source.PatientId && a.Status != AllergyStatus.EnteredInError).ToListAsync(ct);

        // The stay's lab reports: those ordered in its in-patient visit, under the patient's UHID or a duplicate's.
        Guid[] reportPatients = [source.PatientId, .. source.Duplicates];
        var reports = stayVisitId is { } stayVisit
            ? await Reports.CompleteAsync(await StayReports(stayVisit).Where(r => reportPatients.Contains(r.PatientId)).ToListAsync(ct), ct)
            : [];
        Guid[] orders = [.. reports.Select(r => r.LabOrderId).Distinct()];
        var results = orders.Length == 0 ? [] : await Observations.ReleasedResults(r => orders.Contains(r.LabOrderId)).ToListAsync(ct);

        var amender = summary.Status == DischargeSummaryStatus.Amended
            ? await AmenderAsync(
                db.Set<DischargeSummaryAmendment>().Where(a => a.DischargeSummaryId == id).OrderByDescending(a => a.AmendedAt).Select(a => a.AmendedByUserId), ct)
            : null;

        var (visit, encounter) = await EncounterRowsAsync(stayVisitId is null ? summary.EncounterId : null, stayVisitId, null, ct);
        var context = await ContextAsync(source, visit, encounter,
            [summary.PractitionerId, summary.SignedByPractitionerId, amender, .. lines.Select(l => (Guid?)l.PractitionerId)],
            [summary.AdmissionId, .. lines.Select(l => l.AdmissionId)], [], DischargeSummaryPdf, id, ct);
        context = context with { ReferralLabs = await ReferralLabsAsync(results, ct) };
        return AbdmRecords.DischargeSummary(new DischargeSummaryData(context, summary, diagnoses, lines, allergies, reports, results, amender));
    }

    /// <summary>The referral laboratories that performed the results (a laboratory deleted since still did).</summary>
    private async Task<IReadOnlyList<ReferralLabRow>> ReferralLabsAsync(IEnumerable<LabResultRow> results, CancellationToken ct)
    {
        Guid[] ids = [.. results.Select(r => r.ReferralLabId).OfType<Guid>().Distinct()];
        return ids.Length == 0
            ? []
            : await db.Set<ReferralLab>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(l => ids.Contains(l.Id))
                .Select(l => new ReferralLabRow
                {
                    Id = l.Id,
                    Code = l.Code,
                    Name = l.Name,
                    Phone = l.Phone,
                    Email = l.Email,
                    Address = l.Address,
                    AccreditationNumber = l.AccreditationNumber,
                    CreatedAt = l.CreatedAt,
                    UpdatedAt = l.UpdatedAt,
                })
                .ToListAsync(ct);
    }

    /// <summary>The in-patient visit of a discharge summary's stay.</summary>
    private Task<Guid?> StayVisitAsync(Guid summaryId, CancellationToken ct) =>
        (from s in db.Set<DischargeSummary>().AsNoTracking()
         join a in db.Set<Admission>().AsNoTracking() on s.AdmissionId equals a.Id
         where s.Id == summaryId
         select (Guid?)a.VisitId).FirstOrDefaultAsync(ct);

    /// <summary>The released lab reports ordered in a stay's visit, under whichever UHID they are kept.</summary>
    private IQueryable<LabReportRow> StayReports(Guid stayVisit) => Reports.LabReports(r => true).Where(r => r.VisitId == stayVisit);

    /// <summary>
    /// The doctors of the users who wrote, signed off or added to a report (user id to practitioner id): a user who is not
    /// a doctor is left out, and one deleted since still did what the report says.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> DoctorsAsync(IEnumerable<Guid?> users, CancellationToken ct)
    {
        Guid[] ids = [.. users.OfType<Guid>().Distinct()];
        return ids.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await db.Set<User>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                .Where(u => ids.Contains(u.Id) && u.PractitionerId != null)
                .ToDictionaryAsync(u => u.Id, u => u.PractitionerId!.Value, ct);
    }

    /// <summary>The doctor who made the latest of <paramref name="amendments"/> (their users), when that user is a doctor.</summary>
    private async Task<Guid?> AmenderAsync(IQueryable<Guid?> amendments, CancellationToken ct) =>
        await amendments.FirstOrDefaultAsync(ct) is { } userId
            ? await db.Set<User>().AsNoTracking().Where(u => u.Id == userId).Select(u => u.PractitionerId).FirstOrDefaultAsync(ct)
            : null;

    /// <summary>
    /// The encounter rows of a record: the clinician's encounter when there is one, else the visit (with its admission),
    /// else the admission's visit.
    /// </summary>
    private async Task<(VisitRow? Visit, ClinicalEncounterRow? Encounter)> EncounterRowsAsync(Guid? encounterId, Guid? visitId, Guid? admissionId, CancellationToken ct)
    {
        if (encounterId is { } clinicalId && await Encounters.Encounters(e => e.Id == clinicalId).FirstOrDefaultAsync(ct) is { } clinical)
        {
            return (null, clinical);
        }

        if (visitId is null && admissionId is { } admission)
        {
            visitId = await db.Set<Admission>().AsNoTracking().Where(a => a.Id == admission).Select(a => (Guid?)a.VisitId).FirstOrDefaultAsync(ct);
        }

        if (visitId is not { } id || await Encounters.Visits(v => v.Id == id).FirstOrDefaultAsync(ct) is not { } visit)
        {
            return (null, null);
        }

        var stay = (await Encounters.Admissions([id]).ToListAsync(ct)).OrderBy(a => a.Admission.AdmittedAt).LastOrDefault();
        return (stay is null ? visit : visit with { Admission = stay.Admission }, null);
    }

    /// <summary>
    /// The patient, the names every row refers to, the doctors' and facility's master details and the printed PDF
    /// (<paramref name="pdfKind"/> null for none).
    /// </summary>
    private async Task<AbdmContext> ContextAsync(
        AbdmSource source, VisitRow? visit, ClinicalEncounterRow? encounter, IEnumerable<Guid?> practitioners, IEnumerable<Guid?> admissions,
        IEnumerable<Guid?> departments, string? pdfKind, Guid id, CancellationToken ct)
    {
        var ids = new ReferenceIds().Facility(source.FacilityId).Practitioner([.. practitioners]).Admission([.. admissions]).Department([.. departments]);
        if (visit is not null)
        {
            ids.Practitioner(visit.PractitionerId, visit.Admission?.AdmittingPractitionerId, visit.Admission?.ConsultantPractitionerId)
                .Department(visit.DepartmentId, visit.Admission?.DepartmentId).Facility(visit.FacilityId);
        }

        if (encounter is not null)
        {
            ids.Practitioner(encounter.PractitionerId).Department(encounter.DepartmentId).Facility(encounter.FacilityId).Admission(encounter.AdmissionId);
        }

        var patient = await patients.FindRowAsync(source.PatientId, ct) ?? throw new InvalidOperationException("The record's patient is missing.");
        ids.Facility(patient.RegisteredFacilityId);
        Guid[] duplicates = [.. source.Duplicates];
        return new AbdmContext
        {
            Patient = patient,
            DuplicateUhids = duplicates.Length == 0
                ? new Dictionary<Guid, string>()
                : await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
                    .Where(p => duplicates.Contains(p.Id))
                    .ToDictionaryAsync(p => p.Id, p => p.Uhid, ct),
            Visit = visit,
            Encounter = encounter,
            Names = await references.LoadAsync(ids, ct),
            Practitioners = await references.PractitionersAsync(ids.Practitioners, ct),
            Facilities = await references.FacilitiesAsync(ids.Facilities, ct),
            FacilityId = source.FacilityId,
            Pdf = pdfKind is null ? null : await AbdmPdfSource.CreateAsync(renderer, documents.FirstOrDefault(d => d.Kind == pdfKind), id, ct),
        };
    }

    private T Provider<T>()
        where T : IFhirResourceProvider =>
        providers.OfType<T>().Single();
}

/// <summary>
/// The PDF the hospital prints for a record, for its ABDM document. A server that cannot make PDFs (no browser installed)
/// exports every document without one. One that can and fails now (the browser crashed or took too long) does not leave
/// it out, which would make a different document of the same record: the <see cref="PdfRenderException"/> goes up, and
/// the export is answered 503, to be tried again.
/// </summary>
internal static class AbdmPdfSource
{
    public static async Task<AbdmPdf?> CreateAsync(IPdfRenderer renderer, INotificationDocumentSource? source, Guid id, CancellationToken ct) =>
        source is null || renderer.Status.Browser is null ? null
        : await source.CreateAsync(id, ct) is { } file ? new AbdmPdf(file.Content, file.ContentType)
        : null;
}
