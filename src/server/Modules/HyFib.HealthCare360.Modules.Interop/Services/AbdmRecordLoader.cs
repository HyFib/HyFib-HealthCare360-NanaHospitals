using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using Bill = HyFib.HealthCare360.Modules.Billing.Domain.Bill;
using BillStatus = HyFib.HealthCare360.Modules.Billing.Domain.BillStatus;
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

    /// <summary>When the record was made, which names it for the patient when it is linked to their ABHA.</summary>
    public DateTimeOffset RecordedAt { get; init; }

    /// <summary>
    /// What tells the record apart from the patient's other records of its kind on the same day, in the name the patient
    /// sees when it is linked to their ABHA: a vaccine and its dose ("BCG", "Pentavalent, dose 2"), a note's kind and when
    /// it was signed ("Progress note, signed at 3:45 pm"); none for other kinds.
    /// </summary>
    public string? Label { get; init; }
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
    private const string CardiologyReportPdf = "diagnostics.cardiology-report";
    private const string BillPdf = "billing.bill";

    private EncounterProvider Encounters => Provider<EncounterProvider>();
    private ProcedureProvider ProcedureRecords => Provider<ProcedureProvider>();
    private ConditionProvider Conditions => Provider<ConditionProvider>();
    private AllergyIntoleranceProvider Allergies => Provider<AllergyIntoleranceProvider>();
    private ObservationProvider Observations => Provider<ObservationProvider>();
    private MedicationRequestProvider Medications => Provider<MedicationRequestProvider>();
    private DiagnosticReportProvider Reports => Provider<DiagnosticReportProvider>();
    private DocumentReferenceProvider DocumentReferences => Provider<DocumentReferenceProvider>();
    private ImmunizationProvider Doses => Provider<ImmunizationProvider>();

    /// <summary>The record's patient and facility, and whether it is complete; a failure only when it does not exist.</summary>
    public async Task<Result<AbdmSource>> LocateAsync(AbdmRecordType type, Guid id, CancellationToken ct)
    {
        switch (type)
        {
            case AbdmRecordType.Prescription:
            {
                var row = await db.Set<Prescription>().AsNoTracking().Where(p => p.Id == id)
                    .Select(p => new { p.PatientId, p.FacilityId, p.Status, p.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status != PrescriptionStatus.Signed ? "Only a signed prescription that has not been cancelled can be exported." : null)
                    { RecordedAt = row.CreatedAt };
            }

            case AbdmRecordType.LabReport:
            {
                var row = await db.Set<LabReport>().AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.PatientId, r.FacilityId, r.Status, r.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status is not (LabReportStatus.Released or LabReportStatus.Amended) ? "Only a released lab report can be exported." : null)
                    { RecordedAt = row.CreatedAt };
            }

            case AbdmRecordType.RadiologyReport:
            {
                var row = await db.Set<RadiologyReport>().AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.PatientId, r.FacilityId, r.Status, r.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id) : new AbdmSource(row.PatientId, row.FacilityId, RadiologyReportNotExportable(row.Status)) { RecordedAt = row.CreatedAt };
            }

            case AbdmRecordType.CardiologyReport:
            {
                var row = await db.Set<CardiologyReport>().AsNoTracking().Where(r => r.Id == id)
                    .Select(r => new { r.PatientId, r.FacilityId, r.Status, r.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id) : new AbdmSource(row.PatientId, row.FacilityId, CardiologyReportNotExportable(row.Status)) { RecordedAt = row.CreatedAt };
            }

            case AbdmRecordType.OpConsultation:
            {
                var row = await db.Set<ClinicalEncounter>().AsNoTracking().Where(e => e.Id == id)
                    .Select(e => new { e.PatientId, e.FacilityId, e.Status, e.Type, e.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status == EncounterStatus.InProgress ? "Sign the consultation note before exporting it."
                        : row.Type == EncounterType.InPatient
                            ? "An in-patient note is not exported on its own: its diagnoses and prescriptions are in the stay's discharge summary."
                        : null)
                    { RecordedAt = row.CreatedAt };
            }

            case AbdmRecordType.DischargeSummary:
            {
                var row = await db.Set<DischargeSummary>().AsNoTracking().Where(s => s.Id == id)
                    .Select(s => new { s.PatientId, s.FacilityId, s.Status, s.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        row.Status == DischargeSummaryStatus.Draft ? "Sign the discharge summary before exporting it." : null)
                    { RecordedAt = row.CreatedAt };
            }

            case AbdmRecordType.Immunization:
            {
                var row = await db.Set<Immunization>().AsNoTracking().Where(i => i.Id == id)
                    .Select(i => new { i.PatientId, i.FacilityId, i.IsExternal, i.IsEnteredInError, i.VaccineName, i.DoseNumber, i.CreatedAt })
                    .FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId, ImmunizationNotExportable(row.IsExternal, row.IsEnteredInError))
                    {
                        RecordedAt = row.CreatedAt,
                        Label = DoseLabel(row.VaccineName, row.DoseNumber),
                    };
            }

            case AbdmRecordType.ClinicalNote:
            {
                var row = await db.Set<ClinicalNote>().AsNoTracking().Where(n => n.Id == id)
                    .Select(n => new { n.PatientId, n.FacilityId, n.Type, n.IsSigned, n.SignedAt, n.AmendsNoteId, n.CreatedAt })
                    .FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId,
                        NoteNotExportable(row.Type, isSigned: row.IsSigned && row.SignedAt is not null, isAddendum: row.AmendsNoteId is not null))
                    {
                        // A note is the hospital's record from when it is signed.
                        RecordedAt = row.SignedAt ?? row.CreatedAt,
                        Label = NoteLabel(row.Type, row.SignedAt ?? row.CreatedAt),
                    };
            }

            case AbdmRecordType.HomeReadings:
            {
                var row = await db.Set<RpmEnrollment>().AsNoTracking().Where(e => e.Id == id)
                    .Select(e => new { e.PatientId, e.FacilityId, e.StartDate, e.ProgramId }).FirstOrDefaultAsync(ct);
                if (row is null)
                {
                    return NotFound(type, id);
                }

                var kind = await db.Set<RpmProgram>().AsNoTracking().Where(p => p.Id == row.ProgramId).Select(p => (RpmProgramKind?)p.Kind).FirstOrDefaultAsync(ct);
                var anyReading = await db.Set<RpmReading>().AsNoTracking().AnyAsync(r => r.EnrollmentId == id, ct);
                return new AbdmSource(row.PatientId, row.FacilityId, anyReading ? null : AbdmBundleService.NoReadingsYet)
                {
                    // The programme is the care context, from the day the patient was enrolled.
                    RecordedAt = FhirTime.StartOfDay(row.StartDate),
                    Label = ProgrammeLabel(kind),
                };
            }

            case AbdmRecordType.Invoice:
            {
                var row = await db.Set<Bill>().AsNoTracking().Where(b => b.Id == id)
                    .Select(b => new { b.PatientId, b.FacilityId, b.Status, b.BillNumber, b.FinalizedAt, b.CreatedAt }).FirstOrDefaultAsync(ct);
                return row is null ? NotFound(type, id)
                    : new AbdmSource(row.PatientId, row.FacilityId, InvoiceNotExportable(row.Status, row.BillNumber))
                    {
                        // A bill is the hospital's from when it is finalised under its number.
                        RecordedAt = row.FinalizedAt ?? row.CreatedAt,
                        Label = row.BillNumber,
                    };
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of record HC360 exports.");
        }
    }

    /// <summary>
    /// A monitoring programme as the patient knows it among their records, by its kind ("Hypertension monitoring"),
    /// never its name, which the hospital writes as it likes and ABDM keeps with the care context; a programme of the
    /// hospital's own kind is named by the record's kind ("home readings") alone.
    /// </summary>
    internal static string? ProgrammeLabel(RpmProgramKind? kind) => kind switch
    {
        RpmProgramKind.Hypertension => "Hypertension monitoring",
        RpmProgramKind.Diabetes => "Diabetes monitoring",
        RpmProgramKind.Cardiac => "Heart monitoring",
        RpmProgramKind.PostDischarge => "Monitoring after discharge",
        _ => null,
    };

    /// <summary>
    /// Why a dose is not exported. One marked as entered in error was never given. One copied from the patient's
    /// vaccination card is the record of whoever gave it, which the hospital only wrote down: its ABDM record is that
    /// provider's to share (the FHIR API still serves it, as not the hospital's own record).
    /// </summary>
    internal static string? ImmunizationNotExportable(bool isExternal, bool isEnteredInError) =>
        isEnteredInError ? "A dose marked as entered in error is not exported."
        : isExternal ? "A dose copied from the patient's vaccination card is not exported: it is the record of whoever gave it. Only doses given here are."
        : null;

    /// <summary>
    /// Why a bill is not exported. A draft is only an estimate, with no number yet. A cancelled bill is not one the patient
    /// owes, and one whose payments were refunded after it was cancelled (Refunded) is still a cancelled bill. Finalised,
    /// partly paid and paid bills are exported, as the patient may download them and the hospital shares them on WhatsApp.
    /// </summary>
    internal static string? InvoiceNotExportable(BillStatus status, string? billNumber) => status switch
    {
        BillStatus.Cancelled or BillStatus.Refunded => "A cancelled bill is not exported: the patient does not owe it.",
        BillStatus.Finalized or BillStatus.PartiallyPaid or BillStatus.Paid when !string.IsNullOrWhiteSpace(billNumber) => null,
        _ => "Only a finalised bill can be exported: a draft is an estimate.",
    };

    /// <summary>A dose as the patient knows it: the vaccine, and its number in the series when known ("Pentavalent, dose 2").</summary>
    internal static string DoseLabel(string vaccineName, int? doseNumber) =>
        doseNumber is int dose && dose > 0 ? $"{vaccineName.Trim()}, dose {dose}" : vaccineName.Trim();

    /// <summary>
    /// Why a note is not exported. A counselling note is kept to those caring for the patient: an ABDM consent names the
    /// kinds of record it lets go (its HI types), and none names counselling notes apart from the patient's other health
    /// documents, so one exported would go wherever a consent for those lets them go. An addendum goes with the note it
    /// adds to, which carries every addendum signed to it. A draft is not yet the hospital's record.
    /// </summary>
    internal static string? NoteNotExportable(ClinicalNoteType type, bool isSigned, bool isAddendum) =>
        type == ClinicalNoteType.Counselling
            ? "A counselling note is not exported: an ABDM consent cannot keep counselling notes apart from the patient's other health documents."
        : isAddendum ? "An addendum is exported with the note it adds to. Export that note: it carries every addendum signed to it."
        : !isSigned ? "Sign the note before exporting it."
        : null;

    /// <summary>
    /// A note as the patient knows it among the day's others: its kind and when it was signed, in IST ("Progress note,
    /// signed at 3:45 pm"), never its title, which is the writer's free text and goes no further than the record. A note
    /// of no stated kind is named by the time alone, as the record's kind already says "clinical note".
    /// </summary>
    internal static string NoteLabel(ClinicalNoteType type, DateTimeOffset signedAt) =>
        type == ClinicalNoteType.Other
            ? $"signed at {DocumentFormat.Time(signedAt)}"
            : $"{DocumentReferenceMapper.NoteName(type)}, signed at {DocumentFormat.Time(signedAt)}";

    /// <summary>
    /// The patient's records the export would send, newest first, at most <paramref name="limit"/>: signed out-patient
    /// consultations and discharge summaries, issued prescriptions, released lab, radiology and cardiology reports,
    /// vaccine doses given here, signed clinical notes other than counselling notes and addenda, monitoring programmes
    /// with readings, and finalised bills not cancelled, each named by its number (the same rules as
    /// <see cref="LocateAsync"/>), in every facility of the hospital, each with its <see cref="AbdmSource.Label"/>.
    /// Records kept under a duplicate UHID merged into the patient are not included; they are linked from the record staff
    /// see.
    /// </summary>
    public async Task<IReadOnlyList<(AbdmRecordType Type, Guid Id, DateTimeOffset RecordedAt, string? Label)>> ListExportableAsync(
        Guid patientId, int limit, CancellationToken ct)
    {
        var found = new List<(AbdmRecordType Type, Guid Id, DateTimeOffset RecordedAt, string? Label)>();
        void Add(AbdmRecordType type, IEnumerable<(Guid Id, DateTimeOffset At)> rows) => found.AddRange(rows.Select(r => (type, r.Id, r.At, (string?)null)));

        Add(AbdmRecordType.OpConsultation, (await db.Set<ClinicalEncounter>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Status != EncounterStatus.InProgress && e.Type != EncounterType.InPatient)
            .OrderByDescending(e => e.CreatedAt).Take(limit).Select(e => new { e.Id, e.CreatedAt }).ToListAsync(ct)).Select(r => (r.Id, r.CreatedAt)));
        Add(AbdmRecordType.DischargeSummary, (await db.Set<DischargeSummary>().AsNoTracking()
            .Where(d => d.PatientId == patientId && d.Status != DischargeSummaryStatus.Draft)
            .OrderByDescending(d => d.CreatedAt).Take(limit).Select(d => new { d.Id, d.CreatedAt }).ToListAsync(ct)).Select(r => (r.Id, r.CreatedAt)));
        Add(AbdmRecordType.LabReport, (await db.Set<LabReport>().AsNoTracking()
            .Where(r => r.PatientId == patientId && (r.Status == LabReportStatus.Released || r.Status == LabReportStatus.Amended))
            .OrderByDescending(r => r.CreatedAt).Take(limit).Select(r => new { r.Id, r.CreatedAt }).ToListAsync(ct)).Select(r => (r.Id, r.CreatedAt)));
        Add(AbdmRecordType.RadiologyReport, (await db.Set<RadiologyReport>().AsNoTracking()
            .Where(r => r.PatientId == patientId && r.Status == DiagnosticReportStatus.Released)
            .OrderByDescending(r => r.CreatedAt).Take(limit).Select(r => new { r.Id, r.CreatedAt }).ToListAsync(ct)).Select(r => (r.Id, r.CreatedAt)));
        Add(AbdmRecordType.CardiologyReport, (await db.Set<CardiologyReport>().AsNoTracking()
            .Where(r => r.PatientId == patientId && r.Status == DiagnosticReportStatus.Released)
            .OrderByDescending(r => r.CreatedAt).Take(limit).Select(r => new { r.Id, r.CreatedAt }).ToListAsync(ct)).Select(r => (r.Id, r.CreatedAt)));
        found.AddRange((await db.Set<Immunization>().AsNoTracking()
                .Where(i => i.PatientId == patientId && !i.IsExternal && !i.IsEnteredInError)
                .OrderByDescending(i => i.CreatedAt).Take(limit).Select(i => new { i.Id, i.CreatedAt, i.VaccineName, i.DoseNumber }).ToListAsync(ct))
            .Select(i => (AbdmRecordType.Immunization, i.Id, i.CreatedAt, (string?)DoseLabel(i.VaccineName, i.DoseNumber))));
        found.AddRange((await db.Set<ClinicalNote>().AsNoTracking()
                .Where(n => n.PatientId == patientId && n.IsSigned && n.SignedAt != null && n.AmendsNoteId == null && n.Type != ClinicalNoteType.Counselling)
                .OrderByDescending(n => n.SignedAt).Take(limit).Select(n => new { n.Id, SignedAt = n.SignedAt!.Value, n.Type }).ToListAsync(ct))
            .Select(n => (AbdmRecordType.ClinicalNote, n.Id, n.SignedAt, (string?)NoteLabel(n.Type, n.SignedAt))));
        found.AddRange((await db.Set<RpmEnrollment>().AsNoTracking()
                .Where(e => e.PatientId == patientId && db.Set<RpmReading>().Any(r => r.EnrollmentId == e.Id))
                .Join(db.Set<RpmProgram>(), e => e.ProgramId, p => p.Id, (e, p) => new { e.Id, e.StartDate, p.Kind })
                .OrderByDescending(e => e.StartDate).Take(limit).ToListAsync(ct))
            .Select(e => (AbdmRecordType.HomeReadings, e.Id, FhirTime.StartOfDay(e.StartDate), ProgrammeLabel(e.Kind))));
        found.AddRange((await db.Set<Bill>().AsNoTracking()
                .Where(b => b.PatientId == patientId && b.BillNumber != null && b.FinalizedAt != null
                    && (b.Status == BillStatus.Finalized || b.Status == BillStatus.PartiallyPaid || b.Status == BillStatus.Paid))
                .OrderByDescending(b => b.FinalizedAt).Take(limit).Select(b => new { b.Id, FinalizedAt = b.FinalizedAt!.Value, b.BillNumber }).ToListAsync(ct))
            .Select(b => (AbdmRecordType.Invoice, b.Id, b.FinalizedAt, b.BillNumber)));

        // A signed prescription is issued only where the hospital's DSC rule lets it be (the printout's rule).
        var prescriptions = await db.Set<Prescription>().AsNoTracking()
            .Where(p => p.PatientId == patientId && p.Status == PrescriptionStatus.Signed)
            .OrderByDescending(p => p.CreatedAt).Take(limit).Select(p => new { p.Id, p.CreatedAt }).ToListAsync(ct);
        foreach (var prescription in prescriptions)
        {
            if (await PrescriptionNotIssuedAsync(prescription.Id, ct) is null)
            {
                found.Add((AbdmRecordType.Prescription, prescription.Id, prescription.CreatedAt, null));
            }
        }

        return [.. found.OrderByDescending(r => r.RecordedAt).Take(limit)];
    }

    /// <summary>The answer for a record that does not exist, or is not exported because its patient was deleted.</summary>
    public static Error NotFound(AbdmRecordType type, Guid id) => Error.NotFound(
        type switch
        {
            AbdmRecordType.Prescription => nameof(Prescription),
            AbdmRecordType.LabReport => nameof(LabReport),
            AbdmRecordType.RadiologyReport => nameof(RadiologyReport),
            AbdmRecordType.CardiologyReport => nameof(CardiologyReport),
            AbdmRecordType.OpConsultation => "Encounter",
            AbdmRecordType.DischargeSummary => nameof(DischargeSummary),
            AbdmRecordType.Immunization => nameof(Immunization),
            AbdmRecordType.ClinicalNote => "Note",
            AbdmRecordType.HomeReadings => "Enrollment",
            AbdmRecordType.Invoice => nameof(Bill),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of record HC360 exports."),
        },
        id);

    /// <summary>
    /// Why a radiology report is not exported: only a released one is the hospital's report. A draft, a report waiting for
    /// sign-off and one signed off but not released are still the radiologists' work.
    /// </summary>
    internal static string? RadiologyReportNotExportable(DiagnosticReportStatus status) =>
        status == DiagnosticReportStatus.Released ? null : "Only a released radiology report can be exported.";

    /// <summary>Why a cardiology report is not exported: as a radiology report, only a released one is the hospital's report.</summary>
    internal static string? CardiologyReportNotExportable(DiagnosticReportStatus status) =>
        status == DiagnosticReportStatus.Released ? null : "Only a released cardiology report can be exported.";

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
        AbdmRecordType.CardiologyReport => CardiologyReportAsync(id, source, ct),
        AbdmRecordType.OpConsultation => ConsultationAsync(id, source, ct),
        AbdmRecordType.DischargeSummary => DischargeSummaryAsync(id, source, ct),
        AbdmRecordType.Immunization => ImmunizationAsync(id, source, ct),
        AbdmRecordType.ClinicalNote => ClinicalNoteAsync(id, source, ct),
        AbdmRecordType.Invoice => InvoiceAsync(id, source, ct),
        AbdmRecordType.HomeReadings => throw new InvalidOperationException("Home readings are made into documents a span at a time (HomeReadingsAsync)."),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of record HC360 exports."),
    };

    /// <summary>The day (in IST) the enrolment's latest reading was measured; none when it has none.</summary>
    public async Task<DateOnly?> LatestReadingDayAsync(Guid enrollmentId, CancellationToken ct) =>
        await db.Set<RpmReading>().AsNoTracking().Where(r => r.EnrollmentId == enrollmentId)
            .MaxAsync(r => (DateTimeOffset?)r.MeasuredAt, ct) is { } last ? FhirTime.IstDate(last) : null;

    /// <summary>
    /// The months (their first days, in IST) from the first to the last of the enrolment's readings measured from
    /// <paramref name="from"/> to <paramref name="to"/> (both inclusive, either open), oldest first; none when no reading is.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> ReadingMonthsAsync(Guid enrollmentId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var readings = db.Set<RpmReading>().AsNoTracking()
            .Where(r => r.EnrollmentId == enrollmentId && (from == null || r.MeasuredAt >= from) && (to == null || r.MeasuredAt <= to));
        var first = await readings.MinAsync(r => (DateTimeOffset?)r.MeasuredAt, ct);
        var last = await readings.MaxAsync(r => (DateTimeOffset?)r.MeasuredAt, ct);
        if (first is null || last is null)
        {
            return [];
        }

        var months = new List<DateOnly>();
        var lastMonth = ReadingsSpan.Of(FhirTime.IstDate(last.Value)).Month;
        for (var month = ReadingsSpan.Of(FhirTime.IstDate(first.Value)).Month; month <= lastMonth; month = month.AddMonths(1))
        {
            months.Add(month);
        }

        return months;
    }

    /// <summary>
    /// The documents of the enrolment's readings in a span (<see cref="ReadingsSpan"/>), as the FHIR read API serves them
    /// (<see cref="ObservationProvider.DeviceReadingRowsAsync"/>): one, or for a month of more readings than one document
    /// holds, parts in the order measured (<see cref="AbdmRecords.Parts"/>); none when the span holds no reading.
    /// </summary>
    public async Task<IReadOnlyList<AbdmDocument>> HomeReadingsAsync(Guid enrollmentId, AbdmSource source, ReadingsSpan span, CancellationToken ct)
    {
        var lower = span.From ?? span.Start;
        var end = span.End;
        var to = span.To;
        var rows = await Observations.DeviceReadingRowsAsync(db.Set<RpmReading>().AsNoTracking()
            .Where(r => r.EnrollmentId == enrollmentId && r.MeasuredAt >= lower && r.MeasuredAt < end && (to == null || r.MeasuredAt <= to)), ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var programme = await db.Set<RpmEnrollment>().AsNoTracking().Where(e => e.Id == enrollmentId)
            .Join(db.Set<RpmProgram>(), e => e.ProgramId, p => p.Id, (e, p) => p.Name).FirstOrDefaultAsync(ct);
        var context = await ContextAsync(source, null, null, [], [], [], [], null, enrollmentId, ct);
        var parts = AbdmRecords.Parts(rows);
        return [.. parts.Select((part, i) => AbdmRecords.HomeReadings(
            new HomeReadingsData(context, enrollmentId, programme ?? string.Empty, span, part) { Part = i + 1, Parts = parts.Count }))];
    }

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
        var context = await ContextAsync(source, visit, encounter, [header.PractitionerId], [header.AdmissionId], [], [], PrescriptionPdf, id, ct);
        return AbdmRecords.Prescription(new PrescriptionData(
            context, id, header.PrescriptionNumber, header.PractitionerId, header.PrescribedAt, header.SignedAt, header.Advice, header.CreatedAt, header.UpdatedAt, lines));
    }

    private async Task<AbdmDocument> LabReportAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var report = (await Reports.CompleteAsync(await Reports.LabReports(r => r.Id == id).ToListAsync(ct), ct)).First();
        var results = await Observations.ReleasedResultsAsync(r => r.LabOrderId == report.LabOrderId, ct);
        var (visit, encounter) = await EncounterRowsAsync(null, report.VisitId, null, ct);
        var context = await ContextAsync(source, visit, encounter, [], [], [], [], LabReportPdf, id, ct);
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
        var context = await ContextAsync(source, visit, encounter, [.. doctors.Values.Select(p => (Guid?)p)], [], [], [], RadiologyReportPdf, report.ImagingStudyId, ct);
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
        var procedures = await ProcedureRecords.Procedures(p => p.EncounterId == id).ToListAsync(ct);
        var (visit, encounter) = await EncounterRowsAsync(id, null, null, ct);
        var context = await ContextAsync(source, visit, encounter,
            [note.PractitionerId, note.SignedByPractitionerId, note.AmendedByPractitionerId, note.ReferralPractitionerId, .. lines.Select(l => (Guid?)l.PractitionerId),
                .. procedures.Select(p => p.PerformerPractitionerId)],
            [.. lines.Select(l => l.AdmissionId)], [note.ReferralDepartmentId], [], null, id, ct);
        return AbdmRecords.Consultation(new ConsultationData(context, note, diagnoses, vitals, lines, allergies, procedures));
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

        // The stay's procedures: recorded in one of its encounters, or for the stay (an operation the theatre completed).
        var procedures = await ProcedureRecords.Procedures(p => p.AdmissionId == summary.AdmissionId || (p.EncounterId != null && encounterIds.Contains(p.EncounterId.Value)))
            .ToListAsync(ct);

        // The stay's lab reports: those ordered in its in-patient visit, under the patient's UHID or a duplicate's.
        Guid[] reportPatients = [source.PatientId, .. source.Duplicates];
        var reports = stayVisitId is { } stayVisit
            ? await Reports.CompleteAsync(await StayReports(stayVisit).Where(r => reportPatients.Contains(r.PatientId)).ToListAsync(ct), ct)
            : [];
        Guid[] orders = [.. reports.Select(r => r.LabOrderId).Distinct()];
        var results = orders.Length == 0 ? [] : await Observations.ReleasedResultsAsync(r => orders.Contains(r.LabOrderId), ct);

        var amender = summary.Status == DischargeSummaryStatus.Amended
            ? await AmenderAsync(
                db.Set<DischargeSummaryAmendment>().Where(a => a.DischargeSummaryId == id).OrderByDescending(a => a.AmendedAt).Select(a => a.AmendedByUserId), ct)
            : null;

        var (visit, encounter) = await EncounterRowsAsync(stayVisitId is null ? summary.EncounterId : null, stayVisitId, null, ct);
        var context = await ContextAsync(source, visit, encounter,
            [summary.PractitionerId, summary.SignedByPractitionerId, amender, .. lines.Select(l => (Guid?)l.PractitionerId), .. procedures.Select(p => p.PerformerPractitionerId)],
            [summary.AdmissionId, .. lines.Select(l => l.AdmissionId)], [], [], DischargeSummaryPdf, id, ct);
        context = context with { ReferralLabs = await ReferralLabsAsync(results, ct) };
        return AbdmRecords.DischargeSummary(new DischargeSummaryData(context, summary, diagnoses, lines, allergies, reports, results, amender, procedures));
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

    private async Task<AbdmDocument> CardiologyReportAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var report = (await Reports.CardiologyRowsAsync(r => r.Id == id, ct)).Single();
        var doctors = await DoctorsAsync([report.ReportedBy, report.VerifiedBy], ct);
        var (visit, encounter) = await EncounterRowsAsync(null, report.VisitId, null, ct);

        // The printed report is made from its study, as everything in cardiology is.
        var context = await ContextAsync(source, visit, encounter, [.. doctors.Values.Select(p => (Guid?)p)], [], [], [], CardiologyReportPdf, report.CardiologyStudyId, ct);
        return AbdmRecords.CardiologyReport(new CardiologyReportData(context, report, doctors));
    }

    /// <summary>
    /// A dose given here, as the FHIR read API serves it (who gave it, by name), with the encounter it was given in when
    /// staff recorded one. The hospital prints no immunisation record, so the document has no PDF.
    /// </summary>
    private async Task<AbdmDocument> ImmunizationAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var dose = await Doses.Immunizations(i => i.Id == id).FirstAsync(ct);
        var (visit, encounter) = await EncounterRowsAsync(dose.EncounterId, null, null, ct);
        var context = await ContextAsync(source, visit, encounter, [], [], [], [], null, id, ct);
        return AbdmRecords.Immunization(new ImmunizationData(context, dose));
    }

    /// <summary>
    /// A signed note with every addendum signed to it since, an addendum to an addendum included, from whichever facility
    /// it was written in (<see cref="DocumentReferenceProvider.SignedAddendaAsync"/>), and the encounter or stay the note
    /// was written in. Each part names its own writer, signer and facility. The hospital prints no clinical note, so the
    /// document has no PDF.
    /// </summary>
    private async Task<AbdmDocument> ClinicalNoteAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var note = (await DocumentReferences.SignedNotesAsync(n => n.Id == id, ct)).Single();
        var addenda = new List<ClinicalNoteRow>();
        var seen = new HashSet<Guid> { id };
        for (Guid[] parents = [id]; parents.Length > 0;)
        {
            // An addendum to a counselling note is one too, and stays with it.
            var found = (await DocumentReferences.SignedAddendaAsync(parents, ct))
                .Where(a => a.Type != ClinicalNoteType.Counselling && seen.Add(a.Id))
                .ToList();
            addenda.AddRange(found);
            parents = [.. found.Select(a => a.Id)];
        }

        var (visit, encounter) = await EncounterRowsAsync(note.EncounterId, null, note.AdmissionId, ct);
        List<ClinicalNoteRow> parts = [note, .. addenda];
        var context = await ContextAsync(source, visit, encounter,
            [.. parts.SelectMany(p => new[] { p.AuthorPractitionerId, p.SignedByPractitionerId })],
            [.. parts.Select(p => p.AdmissionId)], [], [.. parts.Select(p => (Guid?)p.FacilityId)], null, id, ct);
        return AbdmRecords.ClinicalNote(new ClinicalNoteData(context, note, addenda));
    }

    /// <summary>
    /// A finalised bill with its lines, the visit or stay it was raised for, the doctors whose services it bills, and the
    /// printed bill (Billing's document, in the patient's language, with the payments received by then).
    /// </summary>
    private async Task<AbdmDocument> InvoiceAsync(Guid id, AbdmSource source, CancellationToken ct)
    {
        var bill = await db.Set<Bill>().AsNoTracking().Include(b => b.Lines).FirstAsync(b => b.Id == id, ct);
        var (visit, encounter) = await EncounterRowsAsync(null, bill.VisitId, bill.AdmissionId, ct);
        var context = await ContextAsync(source, visit, encounter, [.. bill.Lines.Select(l => l.PractitionerId)], [bill.AdmissionId],
            [], [], BillPdf, id, ct);
        return AbdmRecords.Invoice(new InvoiceData(context, bill));
    }

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
    /// The patient, the names every row refers to, the doctors' and facilities' master details (the record's facility, and
    /// <paramref name="facilities"/> where parts of it were written: a note's addenda) and the printed PDF
    /// (<paramref name="pdfKind"/> null for none).
    /// </summary>
    private async Task<AbdmContext> ContextAsync(
        AbdmSource source, VisitRow? visit, ClinicalEncounterRow? encounter, IEnumerable<Guid?> practitioners, IEnumerable<Guid?> admissions,
        IEnumerable<Guid?> departments, IEnumerable<Guid?> facilities, string? pdfKind, Guid id, CancellationToken ct)
    {
        var ids = new ReferenceIds().Facility(source.FacilityId).Facility([.. facilities]).Practitioner([.. practitioners]).Admission([.. admissions])
            .Department([.. departments]);
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
