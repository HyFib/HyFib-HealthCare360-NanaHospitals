using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Documents;
using Modality = HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging.Modality;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>A PDF of the record as the hospital prints it.</summary>
internal sealed record AbdmPdf(byte[] Content, string ContentType = "application/pdf");

/// <summary>
/// What every document needs besides its record: the patient, the encounter rows (a clinician's encounter, or a visit
/// with its admission), the names of the people, departments and facilities the rows refer to, the doctors', the
/// facility's and the referral laboratories' master details, the record's facility and the printed PDF when there is
/// one. Nothing depends on the day of the export: a document says what its record said when it was completed
/// (<see cref="AbdmAsOf"/>).
/// </summary>
internal sealed record AbdmContext
{
    public required PatientRow Patient { get; init; }

    /// <summary>
    /// The UHIDs of the duplicates merged into the patient whose records the document may hold (a lab report is still
    /// kept under the UHID it was made for, and its printed PDF names that one), by patient id.
    /// </summary>
    public IReadOnlyDictionary<Guid, string> DuplicateUhids { get; init; } = new Dictionary<Guid, string>();

    public VisitRow? Visit { get; init; }
    public ClinicalEncounterRow? Encounter { get; init; }
    public required ReferenceDirectory Names { get; init; }
    public IReadOnlyList<PractitionerRow> Practitioners { get; init; } = [];
    public IReadOnlyList<FacilityRow> Facilities { get; init; } = [];

    /// <summary>The referral laboratories that performed tests in the document.</summary>
    public IReadOnlyList<ReferralLabRow> ReferralLabs { get; init; } = [];

    public required Guid FacilityId { get; init; }
    public AbdmPdf? Pdf { get; init; }
}

/// <summary>A signed prescription and its medicine lines.</summary>
internal sealed record PrescriptionData(
    AbdmContext Context,
    Guid Id,
    string Number,
    Guid PractitionerId,
    DateTimeOffset PrescribedAt,
    DateTimeOffset? SignedAt,
    string? Advice,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<PrescriptionItemRow> Lines);

/// <summary>A released lab report (its result ids, test names and collection time filled in) and its released results.</summary>
internal sealed record LabReportData(AbdmContext Context, LabReportRow Report, IReadOnlyList<LabResultRow> Results);

/// <summary>
/// A released radiology report with its imaging study and addenda, and the doctors of the users who wrote it, signed it
/// off and added the addenda (user id to practitioner id: a user who is not a doctor is not in it).
/// </summary>
internal sealed record RadiologyReportData(AbdmContext Context, RadiologyReportRow Report, IReadOnlyDictionary<Guid, Guid> Doctors);

/// <summary>
/// A released cardiology report with its study, and the doctors of the users who wrote it and signed it off (user id to
/// practitioner id: a user who is not a doctor is not in it).
/// </summary>
internal sealed record CardiologyReportData(AbdmContext Context, CardiologyReportRow Report, IReadOnlyDictionary<Guid, Guid> Doctors);

/// <summary>The signed consultation note of a clinician's encounter.</summary>
internal sealed record ConsultationNote
{
    public required Guid Id { get; init; }
    public Guid PractitionerId { get; init; }
    public Guid? SignedByPractitionerId { get; init; }
    public EncounterStatus Status { get; init; }
    public DateTimeOffset? SignedAt { get; init; }
    public DateTimeOffset? AmendedAt { get; init; }

    /// <summary>The doctor who made the latest amendment, when its author is a doctor.</summary>
    public Guid? AmendedByPractitionerId { get; init; }
    public string? ChiefComplaint { get; init; }
    public string? HistoryOfPresentIllness { get; init; }
    public string? Examination { get; init; }
    public string? Assessment { get; init; }
    public string? Plan { get; init; }
    public DateOnly? FollowUpDate { get; init; }
    public string? FollowUpAdvice { get; init; }
    public Guid? ReferralDepartmentId { get; init; }
    public Guid? ReferralPractitionerId { get; init; }
    public string? ReferralExternal { get; init; }
    public string? ReferralReason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>A consultation with its diagnoses, vital signs, prescriptions, procedures and the patient's allergies.</summary>
internal sealed record ConsultationData(
    AbdmContext Context,
    ConsultationNote Note,
    IReadOnlyList<DiagnosisRow> Diagnoses,
    IReadOnlyList<VitalSignsRow> Vitals,
    IReadOnlyList<PrescriptionItemRow> Lines,
    IReadOnlyList<AllergyRow> Allergies,
    IReadOnlyList<ProcedureRow>? Procedures = null);

/// <summary>
/// A signed discharge summary with the stay's diagnoses, prescriptions, lab reports and procedures (operations the
/// theatre completed included), the patient's allergies, and the doctor who made the latest amendment, when its author
/// is a doctor.
/// </summary>
internal sealed record DischargeSummaryData(
    AbdmContext Context,
    DischargeSummaryRow Summary,
    IReadOnlyList<DiagnosisRow> Diagnoses,
    IReadOnlyList<PrescriptionItemRow> Lines,
    IReadOnlyList<AllergyRow> Allergies,
    IReadOnlyList<LabReportRow> LabReports,
    IReadOnlyList<LabResultRow> LabResults,
    Guid? AmendedByPractitionerId = null,
    IReadOnlyList<ProcedureRow>? Procedures = null);

/// <summary>A vaccine dose given here, as the FHIR read API serves it.</summary>
internal sealed record ImmunizationData(AbdmContext Context, ImmunizationRow Dose);

/// <summary>
/// A signed clinical note and the addenda signed to it since (an addendum to one of them included), each as the FHIR read
/// API serves it: its encounter and stay kept only when they are the patient's own, its writer and signer named.
/// </summary>
internal sealed record ClinicalNoteData(AbdmContext Context, ClinicalNoteRow Note, IReadOnlyList<ClinicalNoteRow> Addenda);

/// <summary>
/// The home readings of a monitoring programme that one document holds (<see cref="ReadingsSpan"/>), with the programme's
/// name, each as the FHIR read API serves it: the device that sent it, and who entered one read out on the phone. A month
/// of more readings than one document holds goes in parts (<see cref="AbdmRecords.Parts"/>): this is part
/// <see cref="Part"/> of <see cref="Parts"/>.
/// </summary>
internal sealed record HomeReadingsData(AbdmContext Context, Guid EnrollmentId, string Programme, ReadingsSpan Span, IReadOnlyList<DeviceReadingRow> Readings)
{
    public int Part { get; init; } = 1;
    public int Parts { get; init; } = 1;
}

/// <summary>
/// The readings one document holds: those measured in a calendar month in IST (<see cref="Month"/>, its first day), cut
/// where the dates asked for begin after the month does (<see cref="From"/>) or end before it (<see cref="To"/>), both
/// inclusive as a consent's dates are. A month is the unit, so no document grows with the years a programme runs: a year
/// of readings at a few a day in one document would be megabytes for a PHR app to open.
/// </summary>
internal sealed record ReadingsSpan
{
    private ReadingsSpan(DateOnly month, DateTimeOffset? from, DateTimeOffset? to)
    {
        Month = month;
        From = from;
        To = to;
    }

    public DateOnly Month { get; }

    /// <summary>The first instant it holds, when the dates asked for begin after the month does.</summary>
    public DateTimeOffset? From { get; }

    /// <summary>The last instant it holds, when the dates asked for end before the month does.</summary>
    public DateTimeOffset? To { get; }

    /// <summary>The month's first instant: midnight IST on its first day.</summary>
    public DateTimeOffset Start => FhirTime.StartOfDay(Month);

    /// <summary>The next month's first instant, which the month ends before.</summary>
    public DateTimeOffset End => FhirTime.StartOfDay(Month.AddMonths(1));

    /// <summary>The span as part of a document's identity: the month, and where the dates asked for cut it.</summary>
    public string Key =>
        Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)
        + (From is { } from ? "/from-" + from.UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty)
        + (To is { } to ? "/to-" + to.UtcTicks.ToString(CultureInfo.InvariantCulture) : string.Empty);

    /// <summary>The time it covers: the month's days, or the instants the dates asked for cut it at.</summary>
    public R4.Period Period => new()
    {
        Start = From is { } from ? FhirTime.DateTime(from) : FhirTime.Date(Month),
        End = To is { } to ? FhirTime.DateTime(to) : FhirTime.Date(Month.AddMonths(1).AddDays(-1)),
    };

    /// <summary>
    /// The readings of the month <paramref name="day"/> is in, measured from <paramref name="from"/> to
    /// <paramref name="to"/> (either open): a cut is kept only where it falls inside the month.
    /// </summary>
    public static ReadingsSpan Of(DateOnly day, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var month = new DateOnly(day.Year, day.Month, 1);
        var start = FhirTime.StartOfDay(month);
        var end = FhirTime.StartOfDay(month.AddMonths(1));
        return new ReadingsSpan(month, from is { } f && f > start ? f : null, to is { } t && t < end ? t : null);
    }

    /// <summary>Whether a reading measured then is in it.</summary>
    public bool Holds(DateTimeOffset measuredAt) => measuredAt >= (From ?? Start) && measuredAt < End && (To is null || measuredAt <= To);
}

/// <summary>The names ABDM documents download under: the record's kind, its number or the UHID and day, then <c>-abdm.json</c>.</summary>
internal static class AbdmFiles
{
    public static string Name(string kind, string number, DateOnly? day = null)
    {
        var name = new StringBuilder(kind).Append('-');
        foreach (var c in number.Trim())
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        }

        if (day is { } date)
        {
            name.Append('-').Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return name.Append("-abdm.json").ToString();
    }
}

/// <summary>
/// What a record held when it was completed (<c>asOf</c>: the time it was signed, released, or amended for an amended
/// one), so that exporting it again later gives the same document. The rows are those entered by then (a later allergy,
/// diagnosis, vital sign, prescription or lab report is not in it), each with the status it had then where HC360 keeps
/// when that changed: a diagnosis resolved since was active, a prescription cancelled since was signed, and a lab report
/// completed since was partial, with only the tests released by then. An entry recorded in error since is left out, as
/// never true. The entries carry no change time (<see cref="AbdmRecords"/>): their latest change may be after the date.
/// HC360 keeps no history of other changes (an allergy made inactive, a diagnosis confirmed, an edited reaction, a lab
/// result amended): those show as they are now, and give the document a new version.
/// </summary>
internal static class AbdmAsOf
{
    /// <summary>
    /// Diagnoses recorded by then. One resolved since was active then: resolved on a later day, or on that day after it
    /// (it changed after the time, and resolving it is what changes it).
    /// </summary>
    public static List<DiagnosisRow> Diagnoses(IEnumerable<DiagnosisRow> rows, DateTimeOffset asOf)
    {
        var day = FhirTime.IstDate(asOf);
        return
        [
            .. rows.Where(d => d.CreatedAt <= asOf).Select(d =>
                d.Status == DiagnosisStatus.Resolved && (d.ResolvedOn > day || (d.ResolvedOn == day && d.UpdatedAt > asOf))
                    ? d with { Status = DiagnosisStatus.Active, ResolvedOn = null }
                    : d),
        ];
    }

    public static List<AllergyRow> Allergies(IEnumerable<AllergyRow> rows, DateTimeOffset asOf) =>
        [.. rows.Where(a => a.CreatedAt <= asOf && a.Status != AllergyStatus.EnteredInError)];

    public static List<VitalSignsRow> Vitals(IEnumerable<VitalSignsRow> rows, DateTimeOffset asOf) =>
        [.. rows.Where(v => v.CreatedAt <= asOf && !v.IsEnteredInError)];

    /// <summary>Procedures recorded by then; one corrected since shows as it is now, and one recorded in error is left out.</summary>
    public static List<ProcedureRow> Procedures(IEnumerable<ProcedureRow> rows, DateTimeOffset asOf) =>
        [.. rows.Where(p => p.CreatedAt <= asOf && p.Status != ProcedureStatus.EnteredInError)];

    /// <summary>Lines of prescriptions signed by then and not cancelled by then.</summary>
    public static List<PrescriptionItemRow> Lines(IEnumerable<PrescriptionItemRow> rows, DateTimeOffset asOf) =>
    [
        .. rows.Where(l => l.SignedAt <= asOf && (l.Status == PrescriptionStatus.Signed || (l.Status == PrescriptionStatus.Cancelled && l.CancelledAt > asOf)))
            .Select(l => l.Status == PrescriptionStatus.Cancelled ? l with { Status = PrescriptionStatus.Signed, CancelledAt = null, CancelReason = null } : l),
    ];

    /// <summary>Whether the report had been released by then (its first release: a later one, or an amendment, moves only its latest).</summary>
    public static bool Released(LabReportRow report, DateTimeOffset asOf) => (report.FirstReleasedAt ?? report.ReleasedAt ?? report.CreatedAt) <= asOf;

    /// <summary>
    /// The lab reports released by then, each with the results of the tests released by then. A report with tests
    /// released since was partial then, last released with the results it had; one amended since shows its results as
    /// they are now, and when it was amended.
    /// </summary>
    public static (List<LabReportRow> Reports, List<LabResultRow> Results) LabReports(
        IEnumerable<LabReportRow> reports, IEnumerable<LabResultRow> results, DateTimeOffset asOf)
    {
        var released = results.Where(r => (r.ReleasedAt ?? r.CreatedAt) <= asOf).ToDictionary(r => r.Id);
        var held = new List<LabReportRow>();
        foreach (var report in reports.Where(r => Released(r, asOf)))
        {
            List<LabResultRow> then = [.. report.ResultIds.Select(released.GetValueOrDefault).OfType<LabResultRow>()];
            if (then.Count == report.ResultIds.Count)
            {
                held.Add(report);
            }
            else if (then.Count > 0)
            {
                held.Add(report with
                {
                    ResultIds = [.. then.Select(r => r.Id)],
                    TestNames = [.. then.Select(r => r.TestName).Distinct(StringComparer.Ordinal)],
                    IsPartial = true,
                    ReleasedAt = report.Status == LabReportStatus.Amended ? report.ReleasedAt : then.Max(r => r.ReleasedAt) ?? report.FirstReleasedAt,
                });
            }
        }

        return (held, [.. released.Values]);
    }
}

/// <summary>
/// The contents of each ABDM document type, from the rows of its record (no database: every rule here is unit-tested).
/// The resources come from the FHIR read API's mappers, with literal Practitioner and Organization references, fitted to
/// the IG's profiles by <see cref="AbdmProfiler"/>; the sections are the IG's, in its order, with the record's free text
/// as their narrative. The rows are the record's as of the document's date (<see cref="AbdmAsOf"/>), the time it was
/// signed, released or amended, and a medicine's status (active, completed) is as of that day, so exporting it again
/// later gives the same document.
/// </summary>
internal static class AbdmRecords
{
    /// <summary>
    /// A signed e-prescription (PrescriptionRecord): one MedicationRequest per medicine, and the printed PDF; the advice
    /// in the narrative.
    /// </summary>
    public static AbdmDocument Prescription(PrescriptionData d)
    {
        var people = People.Of(d.Context);
        var date = d.SignedAt ?? d.PrescribedAt;
        var requests = MedicationRequests(d.Lines, people, date);
        var author = people.Practitioner(d.PractitionerId);
        var encounter = Encounter(d.Context, people);

        List<R4.Resource> entries = [.. requests];
        if (d.Context.Pdf is { } pdf)
        {
            entries.Add(new R4.Binary
            {
                Id = FhirIds.Of(d.Id),
                Meta = new R4.Meta { Profile = [AbdmProfiles.Binary] },
                ContentType = pdf.ContentType,
                Data = pdf.Content,
            });
        }

        return new AbdmDocument
        {
            Type = AbdmRecordType.Prescription,
            FileName = AbdmFiles.Name("prescription", d.Number),
            SourceId = d.Id,
            Profile = AbdmProfiles.PrescriptionRecord,
            DocumentType = AbdmCodes.PrescriptionRecord,
            Title = "Prescription record",
            Status = R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(d.CreatedAt, d.UpdatedAt),
            Patient = Patient(d.Context, people, []),
            Encounter = encounter,
            Author = author,
            Attester = author,
            AttestedAt = d.SignedAt,
            Custodian = people.Organization(d.Context.FacilityId),
            Sections = [new AbdmSection(AbdmCodes.PrescriptionRecord, "Prescription record", Lines(Field("Advice", d.Advice)), entries, "Medicines:")],
            Supporting = people.All,
        };
    }

    /// <summary>
    /// A released lab report (DiagnosticReportRecord): the report with its released results, and the printed PDF. The
    /// laboratory (the facility) is the author and interprets the results. A report released with results still to come
    /// is a preliminary document, as its DiagnosticReport is partial.
    /// </summary>
    public static AbdmDocument LabReport(LabReportData d)
    {
        var people = People.Of(d.Context);
        var laboratory = people.Organization(d.Context.FacilityId);
        var (report, results) = LabReportWithResults(d.Report, d.Results, people, d.Context.FacilityId);
        var encounter = Encounter(d.Context, people);
        var released = d.Report.ReleasedAt ?? d.Report.CreatedAt;
        var status = d.Report.Status == LabReportStatus.Amended ? R4.CompositionStatus.Amended
            : d.Report.IsPartial ? R4.CompositionStatus.Preliminary
            : R4.CompositionStatus.Final;

        List<R4.Resource> entries = [report];
        if (d.Context.Pdf is { } pdf)
        {
            entries.Add(PdfDocument(FhirIds.Of(FhirIds.Lab, d.Report.Id), d.Context.Patient.Id, pdf,
                FhirElements.Concept(FhirSystems.Loinc, DiagnosticReportMapper.LaboratoryReportLoinc, "Laboratory report", "Laboratory report"),
                status, released, people.Names.Organization(d.Context.FacilityId), Reference(d.Context)));
        }

        return new AbdmDocument
        {
            Type = AbdmRecordType.LabReport,
            FileName = AbdmFiles.Name("lab-report", d.Report.ReportNumber),
            SourceId = d.Report.Id,
            Profile = AbdmProfiles.DiagnosticReportRecord,
            DocumentType = AbdmCodes.DiagnosticStudiesReport,
            Title = "Laboratory report",
            Status = status,
            Date = released,
            RecordUpdated = FhirElements.Latest(d.Report.CreatedAt, d.Report.UpdatedAt),
            Patient = Patient(d.Context, people, [d.Report.PatientId]),
            Encounter = encounter,
            Author = laboratory,
            Custodian = laboratory,
            Sections = [new AbdmSection(AbdmCodes.LaboratoryReport, "Laboratory report", [], entries)],
            Supporting = [.. results, .. people.All],
        };
    }

    /// <summary>
    /// A released radiology report (DiagnosticReportRecord, the report a DiagnosticReportImaging): the report with its
    /// imaging study, and the printed PDF. The hospital performed the study and issues the report; the radiologists who
    /// wrote it, signed it off and added addenda interpret it, and the one who signed it off is the author and attests it.
    /// An addendum added after the release amends the document, as an amendment does a note: the radiologist who added the
    /// latest one is an author of that version too and attests it, from then (no one does when that user is not a
    /// doctor). An addendum added before the release is part of the report as it was first released.
    /// </summary>
    public static AbdmDocument RadiologyReport(RadiologyReportData d)
    {
        var row = d.Report;
        var people = People.Of(d.Context);
        var hospital = people.Organization(d.Context.FacilityId);
        var released = row.ReleasedAt ?? row.VerifiedAt ?? row.CreatedAt;
        var addenda = row.Addenda.OrderBy(a => a.AddedAt).ToList();
        var latest = addenda.LastOrDefault(a => a.AddedAt > released);
        var date = latest?.AddedAt ?? released;
        var status = latest is null ? R4.CompositionStatus.Final : R4.CompositionStatus.Amended;

        Guid? DoctorOf(Guid? user) => user is { } id && d.Doctors.TryGetValue(id, out var practitioner) ? practitioner : null;
        var signer = people.Find(DoctorOf(row.VerifiedBy));
        var amender = latest is null ? null : people.Find(DoctorOf(latest.AddedBy));

        // Whoever wrote the report, signed it off or added to it interpreted the study: each once, in that order.
        List<R4.ResourceReference> interpreters =
        [
            .. new[] { row.ReportedBy, row.VerifiedBy }.Concat(addenda.Select(a => a.AddedBy)).Select(DoctorOf).OfType<Guid>().Distinct()
                .Select(id => people.Names.Practitioner(id)).OfType<R4.ResourceReference>(),
        ];

        var study = Study(row, interpreters);
        var report = AbdmProfiler.ImagingReport(DiagnosticReportMapper.FromRadiology(row), AbdmCodes.RadiologyService,
            people.Names.Organization(d.Context.FacilityId)!, interpreters,
            new R4.ResourceReference($"ImagingStudy/{study.Id}") { Type = "ImagingStudy", Display = row.StudyName });

        // This version was issued when the report was released, or amended by its latest addendum, and its text is as of then.
        report.Issued = FhirTime.ToMilliseconds(date);
        foreach (var form in report.PresentedForm)
        {
            form.Creation = FhirTime.DateTime(date);
        }

        List<R4.Resource> entries = [report];
        if (d.Context.Pdf is { } pdf)
        {
            entries.Add(PdfDocument(FhirIds.Of(FhirIds.Radiology, row.Id), d.Context.Patient.Id, pdf,
                FhirElements.Concept(FhirSystems.Loinc, DiagnosticReportMapper.ImagingStudyLoinc, "Diagnostic imaging study", "Radiology report"),
                status, date, people.Names.Organization(d.Context.FacilityId), Reference(d.Context)));
        }

        return new AbdmDocument
        {
            Type = AbdmRecordType.RadiologyReport,
            FileName = AbdmFiles.Name("radiology-report", row.AccessionNumber),
            SourceId = row.Id,
            Profile = AbdmProfiles.DiagnosticReportRecord,
            DocumentType = AbdmCodes.DiagnosticStudiesReport,
            Title = "Radiology report",
            Status = status,
            Date = date,

            // An addendum is a row of its own: adding one leaves the report's row as it was.
            RecordUpdated = FhirElements.Latest(row.CreatedAt, [row.UpdatedAt, .. addenda.Select(a => (DateTimeOffset?)a.AddedAt)]),
            Patient = Patient(d.Context, people, [row.PatientId]),
            Encounter = Encounter(d.Context, people),
            Author = (R4.Resource?)signer ?? (R4.Resource?)people.Find(DoctorOf(row.ReportedBy)) ?? hospital,
            AmendedBy = amender,
            Attester = latest is null ? signer : amender,
            AttestedAt = latest is null ? row.VerifiedAt : date,
            Custodian = hospital,
            Sections = [new AbdmSection(AbdmCodes.ImagingReport, "Radiology report", [], entries)],
            Supporting = [study, .. people.All],
        };
    }

    /// <summary>
    /// A signed consultation (OPConsultRecord): complaint and diagnoses, history, examination with the vital signs,
    /// allergies, medicines prescribed, follow-up, the procedures done in the consultation (the IG's Procedure section,
    /// SNOMED CT 371525003) and referral, then the assessment and plan in a section of its own (the
    /// IG has none; its slicing is open at the end). An amended note is attested by the doctor who amended it.
    /// </summary>
    public static AbdmDocument Consultation(ConsultationData d)
    {
        var note = d.Note;
        var date = note.AmendedAt ?? note.SignedAt ?? note.CreatedAt;
        var people = People.Of(d.Context);
        var conditions = Undated(Conditions(AbdmAsOf.Diagnoses(d.Diagnoses, date)));
        var observations = Undated(AbdmAsOf.Vitals(d.Vitals, date).OrderBy(v => v.RecordedAt).ThenBy(v => v.Id)
            .SelectMany(VitalSignObservations.Map).Select(AbdmProfiler.Observation).ToList());
        var hospital = people.Names.Organization(d.Context.FacilityId)!;
        foreach (var observation in observations)
        {
            // Measured at the hospital: the vital-sign rows do not name the nurse, and the IG asks who is responsible.
            observation.Performer = [R4.BaseExtensions.DeepCopy(hospital)];
        }

        var requests = Undated(MedicationRequests(AbdmAsOf.Lines(d.Lines, date), people, date));
        var allergies = Undated(Allergies(AbdmAsOf.Allergies(d.Allergies, date)));
        var procedures = Undated(Procedures(AbdmAsOf.Procedures(d.Procedures ?? [], date), people));
        var author = people.Practitioner(note.SignedByPractitionerId ?? note.PractitionerId);
        var amended = note.Status == EncounterStatus.Amended;
        var amender = amended ? people.Find(note.AmendedByPractitionerId) : null;
        var encounter = Encounter(d.Context, people);

        var referredTo = string.Join(", ", new[]
        {
            people.Names.Department(note.ReferralDepartmentId),
            note.ReferralPractitionerId is { } doctor && people.Names.Practitioners.TryGetValue(doctor, out var referred) ? referred.Name : null,
            FhirElements.Clean(note.ReferralExternal),
        }.OfType<string>());
        var referral = referredTo.Length == 0 ? null : Field($"Referred to {referredTo}", note.ReferralReason) ?? $"Referred to {referredTo}";
        List<AbdmSection> sections =
        [
            new(AbdmCodes.ChiefComplaints, "Chief complaints", Lines(Field("Chief complaint", note.ChiefComplaint)), conditions, "Diagnoses:"),
            new(AbdmCodes.HistoryAndPhysical, "Medical history", Lines(Field("History of present illness", note.HistoryOfPresentIllness)), []),
            new(AbdmCodes.PhysicalExamination, "Physical examination", Lines(Field("Examination", note.Examination)), observations, "Vital signs:"),
            new(AbdmCodes.Allergies, "Allergies", [], allergies),
            new(AbdmCodes.MedicationSummary, "Medications", [], requests),
            new(AbdmCodes.FollowUp, "Follow up", Lines(
                note.FollowUpDate is { } followUp ? $"Follow-up on {FhirTime.Date(followUp)}" : null,
                FhirElements.Clean(note.FollowUpAdvice)), []),
            new(AbdmCodes.ClinicalProcedureReport, "Procedures", [], procedures),
            new(AbdmCodes.Referral, "Referral", Lines(referral), []),
            new(AbdmCodes.AssessmentAndPlan, "Assessment and plan", Lines(Field("Assessment", note.Assessment), Field("Plan", note.Plan)), []),
        ];

        return new AbdmDocument
        {
            Type = AbdmRecordType.OpConsultation,
            FileName = AbdmFiles.Name("consultation", d.Context.Patient.Uhid, FhirTime.IstDate(date)),
            SourceId = note.Id,
            Profile = AbdmProfiles.OpConsultRecord,
            DocumentType = AbdmCodes.ConsultationReport,
            Title = "Consultation report",
            Status = amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(note.CreatedAt, note.UpdatedAt),
            Patient = Patient(d.Context, people, []),
            Encounter = encounter,
            Author = author,
            AmendedBy = amender,
            Attester = amended ? amender : author,
            AttestedAt = amended ? note.AmendedAt : note.SignedAt,
            Custodian = people.Organization(d.Context.FacilityId),
            Sections = sections,
            Supporting = people.All,
        };
    }

    /// <summary>
    /// A signed discharge summary (DischargeSummaryRecord): the stay as the encounter; complaints and the diagnoses
    /// recorded during the stay (the IG has no diagnosis section; its examples put them here); allergies; the stay's lab
    /// reports; the medicines prescribed during the stay, each active or completed as of the summary's date, under the
    /// doctor's own list of medicines on discharge; the procedures done during the stay (operations the theatre completed
    /// included) under the doctor's own words; the care plan; the summary itself (the printed PDF, else
    /// its text); and the hospital course in a section of its own at the end. An amended summary is attested by the doctor
    /// who amended it.
    /// </summary>
    public static AbdmDocument DischargeSummary(DischargeSummaryData d)
    {
        var summary = d.Summary;
        var date = summary.AmendedAt ?? summary.SignedAt ?? summary.CreatedAt;
        var people = People.Of(d.Context);
        var custodian = people.Organization(d.Context.FacilityId);
        var conditions = Undated(Conditions(AbdmAsOf.Diagnoses(d.Diagnoses, date)));
        var requests = Undated(MedicationRequests(AbdmAsOf.Lines(d.Lines, date), people, date));
        var allergies = Undated(Allergies(AbdmAsOf.Allergies(d.Allergies, date)));
        var procedures = Undated(Procedures(AbdmAsOf.Procedures(d.Procedures ?? [], date), people));
        var reports = new List<R4.DiagnosticReport>();
        var results = new List<R4.Observation>();
        var (heldReports, heldResults) = AbdmAsOf.LabReports(d.LabReports, d.LabResults, date);
        var labReports = heldReports.OrderBy(r => r.ReleasedAt).ThenBy(r => r.Id).ToList();
        foreach (var row in labReports)
        {
            var (report, observations) = LabReportWithResults(row, [.. heldResults.Where(r => r.LabOrderId == row.LabOrderId)], people, d.Context.FacilityId);
            reports.Add(report);
            results.AddRange(observations);
        }

        Undated(reports);
        Undated(results);

        var document = AbdmProfiler.DocumentReference(DocumentReferenceMapper.FromDischargeSummary(summary, people.Names));
        if (d.Context.Pdf is { } pdf)
        {
            document.Content = [new R4.DocumentReference.ContentComponent { Attachment = PdfAttachment(pdf, "Discharge summary", summary.AmendedAt ?? summary.SignedAt) }];
        }

        // A summary signed before signers were recorded may name no doctor: the hospital is then its author.
        var signer = summary.SignedByPractitionerId ?? summary.PractitionerId;
        var practitioner = people.Find(signer);
        var amended = summary.Status == DischargeSummaryStatus.Amended;
        var amender = amended ? people.Find(d.AmendedByPractitionerId) : null;
        if (amended)
        {
            // The amended text is this version's, as the Composition's attestation is: whoever amended it authenticates it
            // (no one, when that was not a doctor), and a doctor who did is one of its authors.
            document.Authenticator = amender is null ? null : people.Names.Practitioner(d.AmendedByPractitionerId);
            if (amender is not null && d.AmendedByPractitionerId != signer)
            {
                document.Author.Add(people.Names.Practitioner(d.AmendedByPractitionerId)!);
            }
        }
        List<AbdmSection> sections =
        [
            new(AbdmCodes.ChiefComplaints, "Chief complaints and diagnoses", Lines(
                Field("Presenting complaints", summary.PresentingComplaints),
                Field("Diagnoses", summary.Diagnoses),
                summary.DiagnosisCodes.Count > 0 ? $"ICD-10 codes: {string.Join(", ", summary.DiagnosisCodes)}" : null), conditions,
                "Diagnoses recorded during the stay:"),
            new(AbdmCodes.Allergies, "Allergies", [], allergies),
            new(AbdmCodes.Investigations, "Investigations", Lines(Field("Investigations", summary.InvestigationsSummary)), reports, "Lab reports:"),
            new(AbdmCodes.MedicationHistory, "Medications", Lines(Field("Medications on discharge", summary.Medications)), requests,
                "Prescribed during the stay:"),
            new(AbdmCodes.PastProcedures, "Procedures", Lines(Field("Procedures", summary.Procedures)), procedures, "Recorded during the stay:"),
            new(AbdmCodes.CarePlan, "Care plan", Lines(
                Field("Advice", summary.Advice),
                summary.FollowUpDate is { } followUp ? $"Follow-up on {FhirTime.Date(followUp)}" : null,
                Field("Follow-up instructions", summary.FollowUpInstructions)), []),
            new(AbdmCodes.DischargeSummary, "Discharge summary", [], [document]),
            new(AbdmCodes.HospitalCourse, "Hospital course", Lines(
                Field("Course in hospital", summary.CourseInHospital),
                Field("Condition at discharge", summary.ConditionAtDischarge)), []),
        ];

        var encounter = Encounter(d.Context, people);
        return new AbdmDocument
        {
            Type = AbdmRecordType.DischargeSummary,
            FileName = AbdmFiles.Name("discharge-summary", d.Context.Patient.Uhid, FhirTime.IstDate(summary.DischargedAt ?? summary.SignedAt ?? summary.AdmittedAt ?? summary.CreatedAt)),
            SourceId = summary.Id,
            Profile = AbdmProfiles.DischargeSummaryRecord,
            DocumentType = AbdmCodes.DischargeSummary,
            Title = "Discharge summary",
            Status = amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(summary.CreatedAt, summary.UpdatedAt),
            Patient = Patient(d.Context, people, labReports.Select(r => r.PatientId)),
            Encounter = encounter,
            Author = (R4.Resource?)practitioner ?? custodian,
            AmendedBy = amender,
            Attester = amended ? amender : practitioner,
            AttestedAt = amended ? summary.AmendedAt : summary.SignedAt,
            Custodian = custodian,
            Sections = sections,
            Supporting = [.. results, .. people.All],
        };
    }

    /// <summary>
    /// The patient, with the UHIDs of merged duplicates that records in the document are still kept under
    /// (<paramref name="recordPatients"/>: the patients the records name) as old ones.
    /// </summary>
    private static R4.Patient Patient(AbdmContext context, People people, IEnumerable<Guid> recordPatients)
    {
        var patient = PatientMapper.Map(context.Patient, people.Names);
        var former = recordPatients.Select(context.DuplicateUhids.GetValueOrDefault).OfType<string>();
        foreach (var uhid in former.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var identifier = PatientMapper.Uhid(uhid);
            identifier.Use = R4.Identifier.IdentifierUse.Old;
            patient.Identifier.Add(identifier);
        }

        return AbdmProfiler.Patient(patient);
    }

    /// <summary>
    /// The record's encounter. A consultation's link to the visit it is part of is left out: the visit is not in the
    /// document, and a link to it could say no more than that it is an encounter.
    /// </summary>
    private static R4.Encounter? Encounter(AbdmContext context, People people)
    {
        var encounter = context.Encounter is { } e ? EncounterMapper.FromClinicalEncounter(e, people.Names)
            : context.Visit is { } v ? EncounterMapper.FromVisit(v, people.Names)
            : null;
        if (encounter is null)
        {
            return null;
        }

        encounter.PartOf = null;
        return AbdmProfiler.Encounter(encounter);
    }

    private static R4.ResourceReference? Reference(AbdmContext context) =>
        context.Encounter is { } e ? FhirReferences.Encounter(FhirIds.ClinicalEncounter, e.Id)
        : context.Visit is { } v ? FhirReferences.Encounter(FhirIds.Visit, v.Id)
        : null;

    /// <summary>
    /// Entries held as of the document's date (<see cref="AbdmAsOf"/>) carry no change time: HC360 keeps no history of
    /// them, and a change after the date (a prescription cancelled, a diagnosis resolved, which the document undoes) would
    /// otherwise make a new version of a document that says the same. A change that shows in an entry still does, by the
    /// version's hash.
    /// </summary>
    private static List<T> Undated<T>(List<T> entries)
        where T : R4.Resource
    {
        foreach (var meta in entries.Select(e => e.Meta).OfType<R4.Meta>())
        {
            meta.LastUpdated = null;
        }

        return entries;
    }

    // Every list is in an order of its own, the record's id last, so that the document does not depend on the order the
    // database returned its rows in.
    private static List<R4.Condition> Conditions(IReadOnlyList<DiagnosisRow> diagnoses) =>
    [
        .. diagnoses.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.RecordedAt).ThenBy(d => d.Id)
            .Select(d => AbdmProfiler.Condition(ConditionMapper.Map(d))),
    ];

    /// <summary>
    /// Procedures in the order they were done, as the FHIR read API serves them under the IG's Procedure profile: by name
    /// (the IG allows SNOMED CT codes only), the performer a practitioner of the document or the name written.
    /// </summary>
    private static List<R4.Procedure> Procedures(IReadOnlyList<ProcedureRow> procedures, People people) =>
    [
        .. procedures.OrderBy(p => p.PerformedAt).ThenBy(p => p.Id).Select(p => AbdmProfiler.Procedure(ProcedureMapper.Map(p, people.Names))),
    ];

    /// <summary>Allergies, except those recorded in error.</summary>
    private static List<R4.AllergyIntolerance> Allergies(IReadOnlyList<AllergyRow> allergies) =>
    [
        .. allergies.Where(a => a.Status != AllergyStatus.EnteredInError).OrderBy(a => a.RecordedAt).ThenBy(a => a.Id)
            .Select(a => AbdmProfiler.Allergy(AllergyIntoleranceMapper.Map(a))),
    ];

    /// <summary>The medicines, each active or completed as of <paramref name="asOf"/> (its day in IST).</summary>
    private static List<R4.MedicationRequest> MedicationRequests(IReadOnlyList<PrescriptionItemRow> lines, People people, DateTimeOffset asOf) =>
        [.. lines.OrderBy(l => l.SignedAt ?? l.PrescribedAt).ThenBy(l => l.PrescriptionId).ThenBy(l => l.Sequence).ThenBy(l => l.Id)
            .Select(l => AbdmProfiler.MedicationRequest(MedicationRequestMapper.Map(l, people.Names, FhirTime.IstDate(asOf))))];

    /// <summary>
    /// A lab report and its results, in the report's order. Who performed each test (the IG's general Observation profile
    /// asks who did): the referral laboratory it was sent out to, as the printed report says, else the hospital's
    /// laboratory, which also issues the report and verified every result.
    /// </summary>
    private static (R4.DiagnosticReport Report, List<R4.Observation> Results) LabReportWithResults(
        LabReportRow row, IReadOnlyList<LabResultRow> results, People people, Guid laboratoryId)
    {
        var laboratory = people.Names.Organization(laboratoryId)!;
        var byId = results.ToDictionary(r => r.Id);
        var observations = new List<R4.Observation>();
        foreach (var id in row.ResultIds)
        {
            if (byId.TryGetValue(id, out var result))
            {
                var observation = AbdmProfiler.Observation(LabObservations.Map(result));
                observation.Performer = [people.ReferralLab(result.ReferralLabId) ?? R4.BaseExtensions.DeepCopy(laboratory)];
                observations.Add(observation);
            }
        }

        var report = AbdmProfiler.LabReport(DiagnosticReportMapper.FromLab(row), laboratory, observations);
        return (report, observations);
    }

    /// <summary>
    /// A released cardiology report (DiagnosticReportRecord): an ECG, echo, TMT, Holter or ABPM report as the IG's
    /// DiagnosticReportImaging, which it gives to cardiology as to radiology, with the cardiology department's service
    /// section and the LOINC study it is of where LOINC names one (<see cref="DiagnosticReportMapper.CardiologyStudyCoding"/>);
    /// the measurements are in its text, as on the printed report. An ECG, a TMT or a Holter is a recording, not an imaging
    /// study, and HC360 keeps no echo series UIDs, so the report names no ImagingStudy. The hospital performed the study and
    /// issues the report; the cardiologists who wrote it and signed it off interpret it, and the one who signed it off is
    /// the author and attests it. A released cardiology report is not changed afterwards, so the document is final.
    /// </summary>
    public static AbdmDocument CardiologyReport(CardiologyReportData d)
    {
        var row = d.Report;
        var people = People.Of(d.Context);
        var hospital = people.Organization(d.Context.FacilityId);
        var date = row.ReleasedAt ?? row.VerifiedAt ?? row.CreatedAt;

        Guid? DoctorOf(Guid? user) => user is { } id && d.Doctors.TryGetValue(id, out var practitioner) ? practitioner : null;
        var signer = people.Find(DoctorOf(row.VerifiedBy));

        // Whoever wrote the report or signed it off interpreted the study: each once, in that order.
        List<R4.ResourceReference> interpreters =
        [
            .. new[] { row.ReportedBy, row.VerifiedBy }.Select(DoctorOf).OfType<Guid>().Distinct()
                .Select(id => people.Names.Practitioner(id)).OfType<R4.ResourceReference>(),
        ];

        var report = AbdmProfiler.ImagingReport(DiagnosticReportMapper.FromCardiology(row), AbdmCodes.CardiologyService,
            people.Names.Organization(d.Context.FacilityId)!, interpreters, study: null);
        report.Issued = FhirTime.ToMilliseconds(date);
        foreach (var form in report.PresentedForm)
        {
            form.Creation = FhirTime.DateTime(date);
        }

        List<R4.Resource> entries = [report];
        if (d.Context.Pdf is { } pdf)
        {
            var study = DiagnosticReportMapper.CardiologyStudyCoding(row.StudyType, row.StudyName);
            entries.Add(PdfDocument(FhirIds.Of(FhirIds.Cardiology, row.Id), d.Context.Patient.Id, pdf,
                study is null
                    ? FhirElements.Concept(FhirSystems.Loinc, CardiologyStudiesLoinc, "Cardiology studies (set)", "Cardiology report")
                    : FhirElements.Concept(FhirSystems.Loinc, study.Code!, study.Display, "Cardiology report"),
                R4.CompositionStatus.Final, date, people.Names.Organization(d.Context.FacilityId), Reference(d.Context)));
        }

        return new AbdmDocument
        {
            Type = AbdmRecordType.CardiologyReport,
            FileName = AbdmFiles.Name("cardiology-report", row.AccessionNumber),
            SourceId = row.Id,
            Profile = AbdmProfiles.DiagnosticReportRecord,
            DocumentType = AbdmCodes.DiagnosticStudiesReport,
            Title = "Cardiology report",
            Status = R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(row.CreatedAt, [row.UpdatedAt]),
            Patient = Patient(d.Context, people, [row.PatientId]),
            Encounter = Encounter(d.Context, people),
            Author = (R4.Resource?)signer ?? (R4.Resource?)people.Find(DoctorOf(row.ReportedBy)) ?? hospital,
            Attester = signer,
            AttestedAt = row.VerifiedAt,
            Custodian = hospital,
            Sections = [new AbdmSection(AbdmCodes.ImagingReport, "Cardiology report", [], entries)],
            Supporting = [.. people.All],
        };
    }

    /// <summary>LOINC's "Date vaccine due": the date an immunisation recommendation is for.</summary>
    private const string DateVaccineDueLoinc = "30980-7";

    /// <summary>
    /// A vaccine dose given here (ImmunizationRecord): the dose as the FHIR API serves it (the vaccine as the SNOMED CT
    /// product of the guide's value set when its card code is known, the batch, expiry, manufacturer, site, route and dose
    /// number, and who gave it, by name), and, when staff wrote when the next dose is due, the hospital's recommendation
    /// of it (<see cref="NextDose"/>). The facility where it was given is the author and the custodian; the nurse or
    /// doctor who gave it is the dose's administering provider. Its date is when it was recorded: a dose is not changed
    /// afterwards, only marked as entered in error, and then not exported (<c>AbdmRecordLoader</c>).
    /// </summary>
    public static AbdmDocument Immunization(ImmunizationData d)
    {
        var row = d.Dose;
        var people = People.Of(d.Context);
        var hospital = people.Organization(d.Context.FacilityId);
        var encounter = Encounter(d.Context, people);
        var dose = AbdmProfiler.Immunization(ImmunizationMapper.Map(row));

        // A dose recorded in an encounter the document does not hold does not point to it: a reference to nothing could
        // say no more than that it is an encounter.
        if (encounter is null)
        {
            dose.Encounter = null;
        }

        List<R4.Resource> entries = [dose];
        if (row.NextDueOn is { } due)
        {
            entries.Add(NextDose(row, dose, due, people.Names.Organization(d.Context.FacilityId)!));
        }

        return new AbdmDocument
        {
            Type = AbdmRecordType.Immunization,

            // Three vaccines are given at birth: the card code tells the files of one day apart.
            FileName = AbdmFiles.Name("immunization", $"{d.Context.Patient.Uhid}-{VaccineCodes.Find(row.VaccineCode)?.Code ?? "vaccine"}", row.AdministeredOn),
            SourceId = row.Id,
            Profile = AbdmProfiles.ImmunizationRecord,
            DocumentType = AbdmCodes.ImmunizationRecord,
            Title = "Immunisation record",
            Status = R4.CompositionStatus.Final,
            Date = row.CreatedAt,
            RecordUpdated = FhirElements.Latest(row.CreatedAt, row.UpdatedAt),
            Patient = Patient(d.Context, people, [row.PatientId]),
            Encounter = encounter,
            Author = hospital,
            Custodian = hospital,
            Sections = [new AbdmSection(AbdmCodes.ImmunizationRecord, "Immunisation record", [], entries)],
            Supporting = [.. people.All],
        };
    }

    /// <summary>
    /// The next dose staff wrote is due (ImmunizationRecommendation): the same vaccine, due that day (LOINC "Date vaccine
    /// due"), the dose after this one when its number is known, on the strength of this dose. The hospital recommends it,
    /// as of when the dose was recorded.
    /// </summary>
    private static R4.ImmunizationRecommendation NextDose(ImmunizationRow row, R4.Immunization dose, DateOnly due, R4.ResourceReference hospital) => new()
    {
        Id = dose.Id,
        Meta = new R4.Meta { Profile = [AbdmProfiles.ImmunizationRecommendation] },
        Patient = FhirReferences.Patient(row.PatientId),
        Date = FhirTime.DateTime(row.CreatedAt),
        Authority = R4.BaseExtensions.DeepCopy(hospital),
        Recommendation =
        [
            new R4.ImmunizationRecommendation.RecommendationComponent
            {
                VaccineCode = [R4.BaseExtensions.DeepCopy(dose.VaccineCode)],
                ForecastStatus = FhirElements.Concept(FhirSystems.ImmunizationRecommendationStatus, "due", "Due"),
                DateCriterion =
                [
                    new R4.ImmunizationRecommendation.DateCriterionComponent
                    {
                        Code = FhirElements.Concept(FhirSystems.Loinc, DateVaccineDueLoinc, "Date vaccine due"),
                        Value = FhirTime.Date(due),
                    },
                ],
                DoseNumber = row.DoseNumber is int number && number > 0 ? new R4.PositiveInt(number + 1) : null,
                SupportingImmunization = [new R4.ResourceReference($"Immunization/{dose.Id}") { Type = "Immunization" }],
            },
        ],
    };

    /// <summary>
    /// A signed clinical note (HealthDocumentRecord): the note as the FHIR API serves it (LOINC's document type for its
    /// kind, the text exactly as signed, who wrote and signed it, the facility that keeps it, its encounter), then each
    /// addendum signed to it since, oldest first, which appends to the note it adds to. The hospital prints no clinical
    /// note, so each document is the signed text, and the section's narrative is that text: the note's, then each
    /// addendum's, each after who signed it and when. The doctor who wrote the note is its author (the facility, for a
    /// note written by staff who are not doctors), and the doctor who signed it attests it; once an addendum is signed the
    /// note is amended, dated and attested as of the latest addendum, whose writer is an author of that version too.
    /// </summary>
    public static AbdmDocument ClinicalNote(ClinicalNoteData d)
    {
        var note = d.Note;
        var people = People.Of(d.Context);
        var hospital = people.Organization(d.Context.FacilityId);
        List<ClinicalNoteRow> parts = [note, .. d.Addenda.OrderBy(a => a.SignedAt).ThenBy(a => a.Id)];
        var latest = parts[^1];
        var amended = parts.Count > 1;
        var kind = DocumentReferenceMapper.NoteName(note.Type);
        var encounter = Encounter(d.Context, people);
        var documents = new List<R4.Resource>();
        var text = new List<string>();
        foreach (var part in parts)
        {
            var document = AbdmProfiler.DocumentReference(DocumentReferenceMapper.FromClinicalNote(part, people.Names));

            // A note written in an encounter the document does not hold does not point to it (its context is that encounter
            // alone): a reference to nothing could say no more than that it is an encounter.
            if (encounter is null)
            {
                document.Context = null;
            }

            documents.Add(document);
            text.Add(Signed(part.Id == note.Id ? kind : "Addendum", part, people.Names));
            text.Add(part.Body);
        }

        var attester = people.Find(latest.SignedByPractitionerId);
        return new AbdmDocument
        {
            Type = AbdmRecordType.ClinicalNote,
            FileName = AbdmFiles.Name(kind.ToLowerInvariant().Replace(' ', '-'), d.Context.Patient.Uhid, FhirTime.IstDate(note.SignedAt)),
            SourceId = note.Id,
            Profile = AbdmProfiles.HealthDocumentRecord,
            DocumentType = AbdmCodes.RecordArtifact,
            Title = kind,
            Status = amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Date = latest.SignedAt,
            RecordUpdated = parts.Max(p => FhirElements.Latest(p.CreatedAt, p.UpdatedAt)),
            Patient = Patient(d.Context, people, [note.PatientId]),
            Encounter = encounter,
            Author = (R4.Resource?)Writer(note, people) ?? hospital,
            AmendedBy = amended ? Writer(latest, people) : null,
            Attester = attester,
            AttestedAt = attester is null ? null : latest.SignedAt,
            Custodian = hospital,
            Sections = [new AbdmSection(AbdmCodes.RecordArtifact, FhirElements.Clean(note.Title) ?? kind, text, documents) { TextIsEntries = true }],
            Supporting = [.. people.All],
        };
    }

    /// <summary>The doctor who wrote a note, else the doctor who signed it, when the document knows them.</summary>
    private static R4.Practitioner? Writer(ClinicalNoteRow note, People people) =>
        people.Find(note.AuthorPractitionerId) ?? people.Find(note.SignedByPractitionerId);

    /// <summary>"Progress note, signed by Dr Asha Rao on 04 Nov 2026, 3:45 pm": who signed a note (by name) and when, in IST.</summary>
    private static string Signed(string what, ClinicalNoteRow note, ReferenceDirectory names)
    {
        var signer = names.Practitioner(note.SignedByPractitionerId)?.Display ?? FhirElements.Clean(note.SignedByUserName);
        var at = DocumentFormat.DateTime(note.SignedAt);
        return signer is null ? $"{what}, signed on {at}" : $"{what}, signed by {signer} on {at}";
    }

    /// <summary>The WellnessRecord's type, which the IG fixes as text alone.</summary>
    public const string WellnessRecordType = "Wellness Record";

    /// <summary>
    /// The most readings one document of home readings holds: a few months of a programme's readings at the rate any
    /// programme asks for, and about a megabyte of FHIR. Devices send as often as they are set to, so a month can hold
    /// more; it then goes in parts.
    /// </summary>
    public const int MaxReadingsPerDocument = 1000;

    /// <summary>
    /// The readings in the order they were measured, in parts of at most <see cref="MaxReadingsPerDocument"/>: one part
    /// when they fit. Readings measured at the same instant stay in one part, so a blood pressure's systolic and diastolic
    /// readings are never sent apart.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DeviceReadingRow>> Parts(IEnumerable<DeviceReadingRow> readings)
    {
        var parts = new List<IReadOnlyList<DeviceReadingRow>>();
        var part = new List<DeviceReadingRow>();
        foreach (var instant in readings.OrderBy(r => r.MeasuredAt).ThenBy(r => r.Id).GroupBy(r => r.MeasuredAt))
        {
            if (part.Count > 0 && part.Count + instant.Count() > MaxReadingsPerDocument)
            {
                parts.Add(part);
                part = [];
            }

            part.AddRange(instant);
        }

        if (part.Count > 0)
        {
            parts.Add(part);
        }

        return parts;
    }

    /// <summary>
    /// The IG's WellnessRecord sections a home reading goes in, each with the profile it asks of its entries: blood
    /// pressure, heart rate, oxygen saturation and temperature are vital signs; weight is a body measurement; a glucometer
    /// reading is a general assessment (the IG's value set for it names blood glucose); an ECG patch's rhythm is none of
    /// those, so it goes in the section for other observations.
    /// </summary>
    internal static (string Title, string Profile) WellnessSection(RpmReadingType type) => type switch
    {
        RpmReadingType.SystolicBp or RpmReadingType.DiastolicBp or RpmReadingType.HeartRate or RpmReadingType.SpO2 or RpmReadingType.Temperature
            => ("Vital Signs", AbdmProfiles.ObservationVitalSigns),
        RpmReadingType.Weight => ("Body Measurement", AbdmProfiles.ObservationBodyMeasurement),
        RpmReadingType.Glucose => ("General Assessment", AbdmProfiles.ObservationGeneralAssessment),
        RpmReadingType.EcgRhythm => ("Other Observations", AbdmProfiles.Observation),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a kind of home reading."),
    };

    /// <summary>
    /// A month of the readings a patient took at home in a remote monitoring programme (WellnessRecord), or the part of it
    /// in the dates a consent asks for (<see cref="ReadingsSpan"/>, which the Composition's event period states). Each
    /// reading is as the FHIR API serves it: the patient measured it, with the device named; one read out on the phone
    /// says who entered it; each stood against the limits the care team set (normal, outside them, beyond the critical
    /// ones). They go in the IG's sections for them (<see cref="WellnessSection"/>), in the order they were measured, and
    /// each section's narrative is its readings with the time each was measured in IST, as a chart of them reads. No one
    /// signs a reading: the facility that runs the programme is the author and the custodian, and the document is dated
    /// when it received the latest of them.
    /// </summary>
    public static AbdmDocument HomeReadings(HomeReadingsData d)
    {
        if (d.Readings.Count == 0)
        {
            throw new InvalidOperationException("A document of home readings needs a reading.");
        }

        var people = People.Of(d.Context);
        var hospital = people.Organization(d.Context.FacilityId);
        var rows = d.Readings.ToDictionary(r => FhirIds.Of(FhirIds.DeviceReading, r.Id));
        var sections = new List<AbdmSection>();
        foreach (var group in d.Readings.GroupBy(r => WellnessSection(r.Type)).OrderBy(g => SectionOrder(g.Key.Title)))
        {
            // A blood pressure panel goes under its systolic reading's id (its diastolic one's, taken alone): both were
            // measured at the same instant, which orders it.
            var observations = DeviceReadingObservations.Map([.. group])
                .OrderBy(o => rows[o.Id!].MeasuredAt).ThenBy(o => o.Id, StringComparer.Ordinal)
                .Select(o => AbdmProfiler.Observation(o, group.Key.Profile))
                .ToList();
            var lines = new List<string>();
            foreach (var observation in observations)
            {
                var line = ReadingLine(observation, rows[observation.Id!]);
                observation.Text = AbdmNarrative.Generated([line]);
                lines.Add(line);
            }

            sections.Add(new AbdmSection(null, group.Key.Title, lines, observations) { TextIsEntries = true });
        }

        var received = d.Readings.Max(r => r.ReceivedAt);
        var month = d.Span.Month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        var title = FhirElements.Clean(d.Programme) is { } programme ? $"Home readings: {programme}, {month}" : $"Home readings, {month}";
        var part = d.Parts > 1 ? $"-part-{d.Part}" : string.Empty;
        return new AbdmDocument
        {
            Type = AbdmRecordType.HomeReadings,
            FileName = AbdmFiles.Name("home-readings", $"{d.Context.Patient.Uhid}-{d.Span.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)}{part}"),
            SourceId = d.EnrollmentId,

            // A month's first part keeps the month's identity as the month grows into more parts.
            Part = d.Part > 1 ? $"{d.Span.Key}/part-{d.Part}" : d.Span.Key,
            Profile = AbdmProfiles.WellnessRecord,
            DocumentType = null,
            TypeText = WellnessRecordType,
            Title = d.Parts > 1 ? $"{title} (part {d.Part} of {d.Parts})" : title,
            Status = R4.CompositionStatus.Final,
            Covers = d.Span.Period,
            Date = received,
            RecordUpdated = received,
            Patient = Patient(d.Context, people, d.Readings.Select(r => r.PatientId)),
            Author = hospital,
            Custodian = hospital,
            Sections = sections,
            Supporting = [.. people.All],
        };
    }

    /// <summary>The IG's order of the WellnessRecord sections.</summary>
    private static int SectionOrder(string title) => title switch
    {
        "Vital Signs" => 0,
        "Body Measurement" => 1,
        "General Assessment" => 2,
        _ => 3,
    };

    /// <summary>
    /// "25 Sep 2026, 7:30 am: Blood pressure: 150 / 95 mmHg, outside the limits set for the patient": a reading as a
    /// chart of them reads, when it was measured in IST, how it stood when it was not normal, and who entered one read out
    /// on the phone.
    /// </summary>
    private static string ReadingLine(R4.Observation observation, DeviceReadingRow row)
    {
        var what = observation.Value is R4.CodeableConcept rhythm
            ? $"{observation.Code.Text}: {rhythm.Text}"
            : AbdmNarrative.Summary(observation) ?? observation.Code.Text;
        var stood = observation.Interpretation.FirstOrDefault()?.Coding.FirstOrDefault()?.Code switch
        {
            "A" => ", outside the limits set for the patient",
            "AA" => ", beyond the critical limits set for the patient",
            _ => string.Empty,
        };
        var entered = row.Source != ReadingSource.Manual ? string.Empty
            : FhirElements.Clean(row.RecordedByUserName) is { } who ? $" (read out by the patient, entered by {who})"
            : " (read out by the patient, entered by hand)";
        return $"{DocumentFormat.DateTime(row.MeasuredAt)}: {what}{stood}{entered}";
    }

    /// <summary>LOINC's document type for cardiology studies, for the printed copy of one LOINC names no study for.</summary>
    private const string CardiologyStudiesLoinc = "26441-6";

    /// <summary>
    /// The study a radiology report is of (the IG's ImagingStudy): its accession number and, once PACS holds its images,
    /// their study instance UID and how many series and images there are; its modality as HC360's worklist sends it; the
    /// radiologists who interpreted it. A series needs the UID PACS gave it, which HC360 does not keep, so the body part,
    /// which DICOM records per series, is in the narrative. It carries no change time: a change to the study's row that
    /// does not show in it (its charge, a technician's note) is not a new version of the report.
    /// </summary>
    private static R4.ImagingStudy Study(RadiologyReportRow row, IReadOnlyList<R4.ResourceReference> interpreters)
    {
        var study = new R4.ImagingStudy
        {
            Id = FhirIds.Of(FhirIds.Radiology, row.ImagingStudyId),
            Meta = new R4.Meta { Profile = [AbdmProfiles.ImagingStudy] },
            Status = R4.ImagingStudy.ImagingStudyStatus.Available,
            Modality = DicomModality(row.Modality) is { } modality ? [modality] : [],
            Subject = FhirReferences.Patient(row.PatientId),
            Encounter = row.VisitId is { } visit ? FhirReferences.Encounter(FhirIds.Visit, visit) : null,
            Started = (row.StartedAt ?? row.AcquiredAt) is { } started ? FhirTime.DateTime(started) : null,
            Interpreter = [.. interpreters.Select(i => R4.BaseExtensions.DeepCopy(i))],
            NumberOfSeries = row.SeriesCount,
            NumberOfInstances = row.InstanceCount,
            Description = row.StudyName,
        };

        if (DicomUid(row.StudyInstanceUid) is { } uid)
        {
            study.Identifier.Add(new R4.Identifier(AbdmSystems.DicomUid, "urn:oid:" + uid));
        }

        study.Identifier.Add(FhirElements.Identifier(FhirSystems.AccessionNumber, row.AccessionNumber, "ACSN", "Accession ID"));
        var images = row.SeriesCount is { } series && row.InstanceCount is { } instances
            ? $"In the hospital's PACS: {series} series, {instances} image{(instances == 1 ? string.Empty : "s")}"
            : null;
        study.Text = AbdmNarrative.Generated(
            Lines(row.StudyName, Field("Modality", DiagnosticReportMapper.ModalityName(row.Modality)), Field("Body part", row.BodyPart),
                Field("Accession number", row.AccessionNumber), images),
            R4.Narrative.NarrativeStatus.Additional);
        return study;
    }

    /// <summary>
    /// The modality as a DICOM code, with DICOM's name for it, as HC360's worklist sends it: a conventional X-ray as CR,
    /// computed radiography. None for a modality DICOM's acquisition modalities do not name.
    /// </summary>
    private static R4.Coding? DicomModality(Modality modality) => modality switch
    {
        Modality.XR => new(AbdmSystems.Dicom, "CR", "Computed Radiography"),
        Modality.CT => new(AbdmSystems.Dicom, "CT", "Computed Tomography"),
        Modality.MR => new(AbdmSystems.Dicom, "MR", "Magnetic Resonance"),
        Modality.US => new(AbdmSystems.Dicom, "US", "Ultrasound"),
        Modality.MG => new(AbdmSystems.Dicom, "MG", "Mammography"),
        Modality.RF => new(AbdmSystems.Dicom, "RF", "Radiofluoroscopy"),
        Modality.DX => new(AbdmSystems.Dicom, "DX", "Digital Radiography"),
        _ => null,
    };

    /// <summary>A study instance UID as DICOM writes one (numbers joined by dots, at most 64 characters), else none.</summary>
    private static string? DicomUid(string? uid) =>
        uid is { Length: > 0 and <= 64 } && uid.Split('.').All(part => part.Length > 0 && part.All(char.IsAsciiDigit)) ? uid : null;

    private static R4.Attachment PdfAttachment(AbdmPdf pdf, string title, DateTimeOffset? created) => new()
    {
        ContentType = pdf.ContentType,
        Data = pdf.Content,
        SizeUnsignedInt = pdf.Content.Length,
        Title = title,
        Creation = created is { } at ? FhirTime.DateTime(at) : null,
    };

    private static R4.DocumentReference PdfDocument(
        string id, Guid patientId, AbdmPdf pdf, R4.CodeableConcept type, R4.CompositionStatus status, DateTimeOffset created, R4.ResourceReference? author,
        R4.ResourceReference? encounter)
    {
        var document = new R4.DocumentReference
        {
            Id = id,
            Meta = new R4.Meta { Profile = [AbdmProfiles.DocumentReference] },
            Status = R4.DocumentReferenceStatus.Current,
            DocStatus = status,
            Type = type,
            Subject = FhirReferences.Patient(patientId),
            Date = FhirTime.ToMilliseconds(created),
            Description = type.Text,
            Content = [new R4.DocumentReference.ContentComponent { Attachment = PdfAttachment(pdf, type.Text ?? "Report", created) }],
        };

        if (author is not null)
        {
            document.Author.Add(author);
        }

        if (encounter is not null)
        {
            document.Context = new R4.DocumentReference.ContextComponent { Encounter = [encounter] };
        }

        return document;
    }

    private static string? Field(string label, string? value) => FhirElements.Clean(value) is { } text ? $"{label}: {text}" : null;

    private static List<string> Lines(params string?[] lines) => [.. lines.OfType<string>()];

    /// <summary>
    /// The document's names (with literal references) and its Practitioner and Organization resources: the facilities'
    /// and the referral laboratories'.
    /// </summary>
    private sealed record People(
        ReferenceDirectory Names,
        Dictionary<Guid, R4.Practitioner> Practitioners,
        Dictionary<Guid, R4.Organization> Organizations,
        Dictionary<Guid, R4.Organization> ReferralLabs)
    {
        /// <summary>All of them, in order of id.</summary>
        public IReadOnlyList<R4.Resource> All =>
        [
            .. Practitioners.OrderBy(p => p.Key).Select(p => p.Value),
            .. Organizations.Concat(ReferralLabs).OrderBy(o => o.Key).Select(o => o.Value),
        ];

        public static People Of(AbdmContext context) => new(
            context.Names.WithLinks(),
            context.Practitioners.ToDictionary(p => p.Id, AbdmParticipants.Practitioner),
            context.Facilities.ToDictionary(f => f.Id, AbdmParticipants.Organization),
            context.ReferralLabs.ToDictionary(l => l.Id, AbdmParticipants.ReferralLab));

        /// <summary>A reference to the referral laboratory, when there is one and the document knows it.</summary>
        public R4.ResourceReference? ReferralLab(Guid? id) =>
            id is { } lab && ReferralLabs.TryGetValue(lab, out var organization)
                ? new R4.ResourceReference($"Organization/{organization.Id}") { Type = "Organization", Display = organization.Name }
                : null;

        public R4.Practitioner Practitioner(Guid id) =>
            Practitioners.TryGetValue(id, out var practitioner) ? practitioner : throw new InvalidOperationException("The record's doctor is missing.");

        /// <summary>The doctor, when there is one and the document knows them.</summary>
        public R4.Practitioner? Find(Guid? id) => id is { } doctor && Practitioners.TryGetValue(doctor, out var practitioner) ? practitioner : null;

        public R4.Organization Organization(Guid id) =>
            Organizations.TryGetValue(id, out var organization) ? organization : throw new InvalidOperationException("The record's facility is missing.");
    }
}
