using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
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

/// <summary>A consultation with its diagnoses, vital signs, prescriptions and the patient's allergies.</summary>
internal sealed record ConsultationData(
    AbdmContext Context,
    ConsultationNote Note,
    IReadOnlyList<DiagnosisRow> Diagnoses,
    IReadOnlyList<VitalSignsRow> Vitals,
    IReadOnlyList<PrescriptionItemRow> Lines,
    IReadOnlyList<AllergyRow> Allergies);

/// <summary>
/// A signed discharge summary with the stay's diagnoses, prescriptions and lab reports, the patient's allergies, and the
/// doctor who made the latest amendment, when its author is a doctor.
/// </summary>
internal sealed record DischargeSummaryData(
    AbdmContext Context,
    DischargeSummaryRow Summary,
    IReadOnlyList<DiagnosisRow> Diagnoses,
    IReadOnlyList<PrescriptionItemRow> Lines,
    IReadOnlyList<AllergyRow> Allergies,
    IReadOnlyList<LabReportRow> LabReports,
    IReadOnlyList<LabResultRow> LabResults,
    Guid? AmendedByPractitionerId = null);

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
    /// A signed consultation (OPConsultRecord): complaint and diagnoses, history, examination with the vital signs,
    /// allergies, medicines prescribed, follow-up and referral, then the assessment and plan in a section of its own (the
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
        var author = people.Practitioner(note.SignedByPractitionerId ?? note.PractitionerId);
        var amended = note.Status == EncounterStatus.Amended;
        var amender = amended ? people.Find(note.AmendedByPractitionerId) : null;
        var encounter = Encounter(d.Context, people);

        var referredTo = string.Join(", ", new[]
        {
            people.Names.Department(note.ReferralDepartmentId),
            note.ReferralPractitionerId is { } doctor && people.Names.Practitioners.TryGetValue(doctor, out var name) ? name : null,
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
    /// doctor's own list of medicines on discharge; procedures; the care plan; the summary itself (the printed PDF, else
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
            new(AbdmCodes.PastProcedures, "Procedures", Lines(Field("Procedures", summary.Procedures)), []),
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
