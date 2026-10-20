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
/// with its admission), the names of the people, departments and facilities the rows refer to, the doctors' and the
/// facility's master details, the record's facility and the printed PDF when there is one. Nothing depends on the day of
/// the export: a document says what its record says.
/// </summary>
internal sealed record AbdmContext
{
    public required PatientRow Patient { get; init; }
    public VisitRow? Visit { get; init; }
    public ClinicalEncounterRow? Encounter { get; init; }
    public required ReferenceDirectory Names { get; init; }
    public IReadOnlyList<PractitionerRow> Practitioners { get; init; } = [];
    public IReadOnlyList<FacilityRow> Facilities { get; init; } = [];
    public required Guid FacilityId { get; init; }
    public AbdmPdf? Pdf { get; init; }
}

/// <summary>A signed prescription and its medicine lines.</summary>
internal sealed record PrescriptionData(
    AbdmContext Context,
    Guid Id,
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

/// <summary>
/// The contents of each ABDM document type, from the rows of its record (no database: every rule here is unit-tested).
/// The resources come from the FHIR read API's mappers, with literal Practitioner and Organization references, fitted to
/// the IG's profiles by <see cref="AbdmProfiler"/>; the sections are the IG's, in its order, with the record's free text
/// as their narrative. A medicine's status (active, completed) is as of the document's date, the day it was signed,
/// released or amended, so exporting it again later gives the same document.
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
            SourceId = d.Id,
            Profile = AbdmProfiles.PrescriptionRecord,
            DocumentType = AbdmCodes.PrescriptionRecord,
            Title = "Prescription record",
            Status = R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(d.CreatedAt, d.UpdatedAt),
            Patient = Patient(d.Context, people),
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
            SourceId = d.Report.Id,
            Profile = AbdmProfiles.DiagnosticReportRecord,
            DocumentType = AbdmCodes.DiagnosticStudiesReport,
            Title = "Laboratory report",
            Status = status,
            Date = released,
            RecordUpdated = FhirElements.Latest(d.Report.CreatedAt, d.Report.UpdatedAt),
            Patient = Patient(d.Context, people),
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
        var conditions = Conditions(d.Diagnoses);
        var observations = d.Vitals.Where(v => !v.IsEnteredInError).OrderBy(v => v.RecordedAt)
            .SelectMany(VitalSignObservations.Map).Select(AbdmProfiler.Observation).ToList();
        var hospital = people.Names.Organization(d.Context.FacilityId)!;
        foreach (var observation in observations)
        {
            // Measured at the hospital: the vital-sign rows do not name the nurse, and the IG asks who is responsible.
            observation.Performer = [R4.BaseExtensions.DeepCopy(hospital)];
        }

        var requests = MedicationRequests(d.Lines, people, date);
        var allergies = Allergies(d.Allergies);
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
            SourceId = note.Id,
            Profile = AbdmProfiles.OpConsultRecord,
            DocumentType = AbdmCodes.ConsultationReport,
            Title = "Consultation report",
            Status = amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(note.CreatedAt, note.UpdatedAt),
            Patient = Patient(d.Context, people),
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
        var conditions = Conditions(d.Diagnoses);
        var requests = MedicationRequests(d.Lines, people, date);
        var allergies = Allergies(d.Allergies);
        var reports = new List<R4.DiagnosticReport>();
        var results = new List<R4.Observation>();
        foreach (var row in d.LabReports.OrderBy(r => r.ReleasedAt))
        {
            var (report, observations) = LabReportWithResults(row, d.LabResults.Where(r => r.LabOrderId == row.LabOrderId).ToList(), people, d.Context.FacilityId);
            reports.Add(report);
            results.AddRange(observations);
        }

        var document = AbdmProfiler.DocumentReference(DocumentReferenceMapper.FromDischargeSummary(summary, people.Names));
        if (d.Context.Pdf is { } pdf)
        {
            document.Content = [new R4.DocumentReference.ContentComponent { Attachment = PdfAttachment(pdf, "Discharge summary", summary.AmendedAt ?? summary.SignedAt) }];
        }

        // A summary signed before signers were recorded may name no doctor: the hospital is then its author.
        var practitioner = people.Find(summary.SignedByPractitionerId ?? summary.PractitionerId);
        var amended = summary.Status == DischargeSummaryStatus.Amended;
        var amender = amended ? people.Find(d.AmendedByPractitionerId) : null;
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
            SourceId = summary.Id,
            Profile = AbdmProfiles.DischargeSummaryRecord,
            DocumentType = AbdmCodes.DischargeSummary,
            Title = "Discharge summary",
            Status = amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Date = date,
            RecordUpdated = FhirElements.Latest(summary.CreatedAt, summary.UpdatedAt),
            Patient = Patient(d.Context, people),
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

    private static R4.Patient Patient(AbdmContext context, People people) =>
        AbdmProfiler.Patient(PatientMapper.Map(context.Patient, people.Names));

    private static R4.Encounter? Encounter(AbdmContext context, People people) =>
        context.Encounter is { } e ? AbdmProfiler.Encounter(EncounterMapper.FromClinicalEncounter(e, people.Names))
        : context.Visit is { } v ? AbdmProfiler.Encounter(EncounterMapper.FromVisit(v, people.Names))
        : null;

    private static R4.ResourceReference? Reference(AbdmContext context) =>
        context.Encounter is { } e ? FhirReferences.Encounter(FhirIds.ClinicalEncounter, e.Id)
        : context.Visit is { } v ? FhirReferences.Encounter(FhirIds.Visit, v.Id)
        : null;

    private static List<R4.Condition> Conditions(IReadOnlyList<DiagnosisRow> diagnoses) =>
        [.. diagnoses.OrderByDescending(d => d.IsPrimary).ThenBy(d => d.RecordedAt).Select(d => AbdmProfiler.Condition(ConditionMapper.Map(d)))];

    /// <summary>Allergies, except those recorded in error.</summary>
    private static List<R4.AllergyIntolerance> Allergies(IReadOnlyList<AllergyRow> allergies) =>
        [.. allergies.Where(a => a.Status != AllergyStatus.EnteredInError).OrderBy(a => a.RecordedAt).Select(a => AbdmProfiler.Allergy(AllergyIntoleranceMapper.Map(a)))];

    /// <summary>The medicines, each active or completed as of <paramref name="asOf"/> (its day in IST).</summary>
    private static List<R4.MedicationRequest> MedicationRequests(IReadOnlyList<PrescriptionItemRow> lines, People people, DateTimeOffset asOf) =>
        [.. lines.OrderBy(l => l.SignedAt ?? l.PrescribedAt).ThenBy(l => l.PrescriptionId).ThenBy(l => l.Sequence)
            .Select(l => AbdmProfiler.MedicationRequest(MedicationRequestMapper.Map(l, people.Names, FhirTime.IstDate(asOf))))];

    private static (R4.DiagnosticReport Report, List<R4.Observation> Results) LabReportWithResults(
        LabReportRow row, IReadOnlyList<LabResultRow> results, People people, Guid laboratoryId)
    {
        var laboratory = people.Names.Organization(laboratoryId)!;
        var listed = row.ResultIds.Select(id => FhirIds.Of(FhirIds.Lab, id)).ToHashSet(StringComparer.Ordinal);
        var observations = results.Select(r => AbdmProfiler.Observation(LabObservations.Map(r))).Where(o => listed.Contains(o.Id!)).ToList();
        foreach (var observation in observations)
        {
            // The laboratory performed the test (the IG's general Observation profile asks who did).
            observation.Performer = [R4.BaseExtensions.DeepCopy(laboratory)];
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

    /// <summary>The document's names (with literal references) and its Practitioner and Organization resources.</summary>
    private sealed record People(ReferenceDirectory Names, Dictionary<Guid, R4.Practitioner> Practitioners, Dictionary<Guid, R4.Organization> Organizations)
    {
        public IReadOnlyList<R4.Resource> All => [.. Practitioners.Values, .. Organizations.Values];

        public static People Of(AbdmContext context) => new(
            context.Names.WithLinks(),
            context.Practitioners.ToDictionary(p => p.Id, AbdmParticipants.Practitioner),
            context.Facilities.ToDictionary(f => f.Id, AbdmParticipants.Organization));

        public R4.Practitioner Practitioner(Guid id) =>
            Practitioners.TryGetValue(id, out var practitioner) ? practitioner : throw new InvalidOperationException("The record's doctor is missing.");

        /// <summary>The doctor, when there is one and the document knows them.</summary>
        public R4.Practitioner? Find(Guid? id) => id is { } doctor && Practitioners.TryGetValue(doctor, out var practitioner) ? practitioner : null;

        public R4.Organization Organization(Guid id) =>
            Organizations.TryGetValue(id, out var organization) ? organization : throw new InvalidOperationException("The record's facility is missing.");
    }
}
