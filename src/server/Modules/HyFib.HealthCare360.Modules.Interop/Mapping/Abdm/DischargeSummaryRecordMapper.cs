using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Documents;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping.Abdm;

/// <summary>What an ABDM discharge summary is made from: the signed summary and the records it names.</summary>
internal sealed record DischargeSummarySource
{
    public required DischargeSummaryRow Summary { get; init; }

    /// <summary>The patient, with the ABHA number decrypted when it is on file.</summary>
    public required PatientRow Patient { get; init; }

    /// <summary>The facility that holds the summary (the document's custodian).</summary>
    public required FacilityRow Facility { get; init; }

    /// <summary>The doctor who signed the summary, else the treating consultant; null when neither is on file.</summary>
    public PractitionerRow? Author { get; init; }

    /// <summary>The admission's in-patient visit, with the admission; null when it can no longer be read.</summary>
    public VisitRow? Stay { get; init; }

    /// <summary>The catalogue titles of the summary's ICD codes, by code.</summary>
    public IReadOnlyDictionary<string, string> CodeTitles { get; init; } = new Dictionary<string, string>();

    /// <summary>The diagnoses doctors recorded during the stay, by ICD code (the latest of each).</summary>
    public IReadOnlyDictionary<string, RecordedDiagnosis> RecordedDiagnoses { get; init; } = new Dictionary<string, RecordedDiagnosis>();

    /// <summary>Names of the doctors, departments and facilities the stay refers to.</summary>
    public ReferenceDirectory References { get; init; } = ReferenceDirectory.Empty;
}

/// <summary>What a doctor recorded about a diagnosis in the stay's encounters (Clinical <see cref="Diagnosis"/>).</summary>
internal sealed record RecordedDiagnosis(DiagnosisStatus Status, DateOnly? OnsetDate, DateOnly? ResolvedOn);

/// <summary>A section code of a document: SNOMED CT for the sections the profile defines, LOINC for the others.</summary>
internal sealed record SectionCode(string System, string Code, string Display)
{
    public R4.CodeableConcept ToConcept() => new() { Coding = [FhirElements.Coding(System, Code, Display)] };
}

/// <summary>
/// A signed (or amended) discharge summary → an ABDM health record: an NRCeS DocumentBundle whose Composition follows the
/// DischargeSummaryRecord profile (SNOMED CT 373942005), with the patient (UHID, and the ABHA number when on file), the
/// doctor who signed it, the hospital (with its HFR id when on file), the stay as the encounter, a Condition for each
/// ICD-10 code, the advice and follow-up as a care plan, and the whole summary as a document.
/// <para>
/// What the doctor wrote is free text, and stays text: each part of the summary becomes the narrative of a section, as
/// written. Only what HC360 holds in coded form becomes coded entries (the ICD codes, the follow-up date); complaints,
/// procedures and medicines are not turned into coded resources HC360 does not have. The profile's sections are sliced
/// by code and open at the end, so the parts it has no section for (the final diagnosis, the course in hospital) follow
/// its sections, coded with their LOINC section codes.
/// </para>
/// </summary>
internal static class DischargeSummaryRecordMapper
{
    public const string SnomedDischargeSummary = "373942005";

    public static readonly SectionCode ChiefComplaints = new(FhirSystems.Snomed, "422843007", "Chief complaint section");
    public static readonly SectionCode PhysicalExamination = new(FhirSystems.Snomed, "425044008", "Physical exam section");
    public static readonly SectionCode Investigations = new(FhirSystems.Snomed, "721981007", "Diagnostic studies report");
    public static readonly SectionCode Medications = new(FhirSystems.Snomed, "1003606003", "Medication history section");
    public static readonly SectionCode Procedures = new(FhirSystems.Snomed, "1003640003", "History of past procedure section");
    public static readonly SectionCode CarePlan = new(FhirSystems.Snomed, "734163000", "Care plan");
    public static readonly SectionCode Document = new(FhirSystems.Snomed, SnomedDischargeSummary, "Discharge summary");
    public static readonly SectionCode Diagnoses = new(FhirSystems.Loinc, "11535-2", "Hospital discharge Dx Narrative");
    public static readonly SectionCode HospitalCourse = new(FhirSystems.Loinc, "8648-8", "Hospital course Narrative");

    /// <summary>A coded diagnosis: its Condition, what it is called, and its line in the section's text.</summary>
    private sealed record CodedDiagnosis(Guid Id, R4.Condition Condition, string Name, string Line);

    /// <summary>The document, assembled at <paramref name="now"/> and identified by <paramref name="documentId"/>.</summary>
    public static R4.Bundle Map(DischargeSummarySource source, Guid documentId, DateTimeOffset now)
    {
        var s = source.Summary;
        var refs = AbdmParticipants.WithEntries(source.References, source.Facility, source.Author);
        var stayId = source.Stay?.Id ?? s.AdmissionId;
        var diagnoses = CodedDiagnoses(source, stayId);
        var carePlan = CarePlanOf(source, stayId);
        var carePlanId = DocumentBundleBuilder.DerivedId(s.Id, "CarePlan");
        var documentReferenceId = DocumentBundleBuilder.DerivedId(s.Id, "DocumentReference");

        // Each Condition names the stay; the stay does not list them back (Encounter.diagnosis), so the document's
        // references never run in a circle.
        var encounter = source.Stay is { } stay ? EncounterMapper.FromVisit(stay, refs) : StayFromSummary(s, refs);
        encounter.Subject = PatientReference(source);

        var sections = new List<R4.Composition.SectionComponent>();
        Section(sections, ChiefComplaints, "Presenting complaints", new SectionText().Paragraphs(s.PresentingComplaints));
        Section(sections, PhysicalExamination, "Condition at discharge", new SectionText().Paragraphs(s.ConditionAtDischarge));
        Section(sections, Investigations, "Investigations", new SectionText().Paragraphs(s.InvestigationsSummary));
        Section(sections, Medications, "Medicines on discharge", new SectionText().Paragraphs(s.Medications));
        Section(sections, Procedures, "Procedures and surgeries", new SectionText().Paragraphs(s.Procedures));
        if (carePlan is not null)
        {
            Section(sections, CarePlan, "Advice and follow-up",
                new SectionText().Paragraphs(s.Advice).Labelled("Follow-up", FollowUp(s)),
                DocumentBundleBuilder.Reference(carePlanId, carePlan.Title));
        }

        Section(sections, Document, "Discharge summary (full text)", new SectionText(),
            DocumentBundleBuilder.Reference(documentReferenceId, "Discharge summary"));

        // Sections the profile does not define come after its own (its slicing is open at the end).
        Section(sections, Diagnoses, "Final diagnosis",
            new SectionText().Paragraphs(s.Diagnoses).List("Diagnosis codes:", [.. diagnoses.Select(d => d.Line)]),
            [.. diagnoses.Select(d => DocumentBundleBuilder.Reference(d.Id, d.Name))]);
        Section(sections, HospitalCourse, "Course in hospital", new SectionText().Paragraphs(s.CourseInHospital));

        var builder = new DocumentBundleBuilder()
            .Add(s.Id, Composition(source, stayId, sections), AbdmProfiles.DischargeSummaryRecord)
            .Add(source.Patient.Id, PatientMapper.Map(source.Patient, refs), AbdmProfiles.Patient)
            .Add(stayId, encounter, AbdmProfiles.Encounter);
        if (source.Author is { } author)
        {
            builder.Add(author.Id, AbdmParticipants.Practitioner(author), AbdmProfiles.Practitioner);
        }

        builder.Add(source.Facility.Id, AbdmParticipants.Organization(source.Facility), AbdmProfiles.Organization);
        foreach (var diagnosis in diagnoses)
        {
            builder.Add(diagnosis.Id, diagnosis.Condition, AbdmProfiles.Condition);
        }

        if (carePlan is not null)
        {
            builder.Add(carePlanId, carePlan, AbdmProfiles.CarePlan);
        }

        builder.Add(documentReferenceId, DocumentReferenceOf(source, refs, stayId), AbdmProfiles.DocumentReference);
        return builder.Build(documentId, now);
    }

    private static R4.Composition Composition(DischargeSummarySource source, Guid stayId, List<R4.Composition.SectionComponent> sections)
    {
        var s = source.Summary;
        var composition = new R4.Composition
        {
            Meta = FhirElements.Meta(s.CreatedAt, s.UpdatedAt),
            // The summary's own identity, the same for every export and amendment of it (each export is a new Bundle).
            Identifier = new R4.Identifier(FhirSystems.Uri, DocumentBundleBuilder.FullUrl(s.Id)),
            Status = s.Status == DischargeSummaryStatus.Amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Type = new R4.CodeableConcept
            {
                Coding = [FhirElements.Coding(FhirSystems.Snomed, SnomedDischargeSummary, "Discharge summary")],
                Text = "Discharge summary",
            },
            Subject = PatientReference(source),
            Encounter = DocumentBundleBuilder.Reference(stayId),
            Date = FhirTime.DateTime(s.AmendedAt ?? s.SignedAt ?? s.UpdatedAt ?? s.CreatedAt),
            Title = "Discharge summary",
            Custodian = DocumentBundleBuilder.Reference(source.Facility.Id, source.Facility.Name),
            Section = sections,
        };

        if (source.Author is { } author)
        {
            composition.Author.Add(DocumentBundleBuilder.Reference(author.Id, author.DisplayName));
            if (s.SignedAt is { } signed)
            {
                // Signing in HC360 is the doctor's legal authentication of the summary.
                composition.Attester.Add(new R4.Composition.AttesterComponent
                {
                    Mode = R4.Composition.CompositionAttestationMode.Legal,
                    Time = FhirTime.DateTime(signed),
                    Party = DocumentBundleBuilder.Reference(author.Id, author.DisplayName),
                });
            }
        }
        else
        {
            // No doctor on file: the hospital is the author (a Composition must have one).
            composition.Author.Add(DocumentBundleBuilder.Reference(source.Facility.Id, source.Facility.Name));
        }

        return composition;
    }

    /// <summary>
    /// A Condition for each code of the summary: the final diagnoses of the stay (encounter diagnoses, confirmed). The
    /// NRCeS Condition admits ICD-10 and SNOMED CT codings only, each with its display, so an ICD-10 code is coded with its
    /// catalogue title, and any other code (ICD-11, or one whose title is unknown) is written in the text. The clinical
    /// status, onset and resolution are those a doctor recorded for the code during the stay; a final diagnosis nobody
    /// recorded otherwise is active as of the summary, which is what the summary states.
    /// </summary>
    private static List<CodedDiagnosis> CodedDiagnoses(DischargeSummarySource source, Guid stayId)
    {
        var s = source.Summary;
        var diagnoses = new List<CodedDiagnosis>();
        foreach (var code in s.DiagnosisCodes.Select(c => FhirElements.Clean(c)?.ToUpperInvariant()).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            var title = FhirElements.Clean(source.CodeTitles.GetValueOrDefault(code));
            var release = CodeReleases.OfDiagnosisCode(code);
            var label = release == CodeReleases.Icd11 ? "ICD-11" : release == CodeReleases.Icd10 ? "ICD-10" : "code";
            var concept = new R4.CodeableConcept();
            string name;
            if (release == CodeReleases.Icd10 && title is not null)
            {
                name = title;
                concept.Coding.Add(FhirElements.Coding(FhirSystems.Icd10, code, title));
            }
            else
            {
                name = $"{title ?? code} ({label} {code})";
            }

            concept.Text = name;

            var recorded = source.RecordedDiagnoses.GetValueOrDefault(code);
            var resolved = recorded is { Status: DiagnosisStatus.Resolved, ResolvedOn: { } resolvedOn } ? resolvedOn : (DateOnly?)null;
            var condition = new R4.Condition
            {
                Meta = FhirElements.Meta(s.CreatedAt, s.UpdatedAt),
                ClinicalStatus = ConditionMapper.ClinicalStatus(recorded?.Status ?? DiagnosisStatus.Active),
                VerificationStatus = ConditionMapper.VerificationStatus(DiagnosisType.Final),
                Onset = recorded?.OnsetDate is { } onset ? new R4.FhirDateTime(FhirTime.Date(onset)) : null,
                Abatement = resolved is { } abated ? new R4.FhirDateTime(FhirTime.Date(abated)) : null,
                Category = [FhirElements.Concept(FhirSystems.ConditionCategory, ConditionMapper.EncounterDiagnosis, "Encounter Diagnosis")],
                Code = concept,
                Subject = PatientReference(source),
                Encounter = DocumentBundleBuilder.Reference(stayId),
                RecordedDate = s.SignedAt is { } signed ? FhirTime.DateTime(signed) : null,
                Recorder = source.Author is { } author ? DocumentBundleBuilder.Reference(author.Id, author.DisplayName) : null,
            };
            diagnoses.Add(new CodedDiagnosis(DocumentBundleBuilder.DerivedId(s.Id, "Condition " + code), condition, name,
                title is null ? $"{code} ({label})" : $"{code} {title} ({label})"));
        }

        return diagnoses;
    }

    /// <summary>The advice and the follow-up visit as the patient's care plan after discharge; null when the summary has neither.</summary>
    private static R4.CarePlan? CarePlanOf(DischargeSummarySource source, Guid stayId)
    {
        var s = source.Summary;
        var advice = FhirElements.Clean(s.Advice);
        var instructions = FhirElements.Clean(s.FollowUpInstructions);
        if (advice is null && instructions is null && s.FollowUpDate is null)
        {
            return null;
        }

        var plan = new R4.CarePlan
        {
            Meta = FhirElements.Meta(s.CreatedAt, s.UpdatedAt),
            Status = R4.RequestStatus.Active,
            Intent = R4.CarePlan.CarePlanIntent.Plan,
            Title = "Advice and follow-up after discharge",
            Description = advice,
            Subject = PatientReference(source),
            Encounter = DocumentBundleBuilder.Reference(stayId),
            Created = s.SignedAt is { } signed ? FhirTime.DateTime(signed) : null,
            Author = source.Author is { } author ? DocumentBundleBuilder.Reference(author.Id, author.DisplayName) : null,
        };

        if (instructions is not null || s.FollowUpDate is not null)
        {
            plan.Activity.Add(new R4.CarePlan.ActivityComponent
            {
                Detail = new R4.CarePlan.DetailComponent
                {
                    Kind = R4.CarePlan.CarePlanActivityKind.Appointment,
                    Status = R4.CarePlan.CarePlanActivityStatus.NotStarted,
                    Scheduled = s.FollowUpDate is { } date ? new R4.Timing { EventElement = [new R4.FhirDateTime(FhirTime.Date(date))] } : null,
                    Description = instructions,
                },
            });
        }

        return plan;
    }

    /// <summary>The summary as a document: its full text (DocumentReferenceMapper), within this document.</summary>
    private static R4.DocumentReference DocumentReferenceOf(DischargeSummarySource source, ReferenceDirectory refs, Guid stayId)
    {
        var document = DocumentReferenceMapper.FromDischargeSummary(source.Summary, refs);
        document.Type = new R4.CodeableConcept
        {
            Coding =
            [
                FhirElements.Coding(FhirSystems.Loinc, DocumentReferenceMapper.DischargeSummaryLoinc, "Discharge summary"),
                FhirElements.Coding(FhirSystems.Snomed, SnomedDischargeSummary, "Discharge summary"),
            ],
            Text = "Discharge summary",
        };
        document.Subject = PatientReference(source);
        document.Custodian = DocumentBundleBuilder.Reference(source.Facility.Id, source.Facility.Name);
        document.Context ??= new R4.DocumentReference.ContextComponent();
        document.Context.Encounter = [DocumentBundleBuilder.Reference(stayId)];
        return document;
    }

    /// <summary>
    /// The stay when its admission can no longer be read: an in-patient encounter with the summary's own admission and
    /// discharge times, whose status is unknown.
    /// </summary>
    private static R4.Encounter StayFromSummary(DischargeSummaryRow s, ReferenceDirectory refs) => new()
    {
        Meta = FhirElements.Meta(s.CreatedAt, s.UpdatedAt),
        Status = R4.Encounter.EncounterStatus.Unknown,
        Class = EncounterMapper.Class(VisitType.InPatient),
        Period = s.AdmittedAt is null && s.DischargedAt is null
            ? null
            : new R4.Period
            {
                Start = s.AdmittedAt is { } admitted ? FhirTime.DateTime(admitted) : null,
                End = s.DischargedAt is { } discharged ? FhirTime.DateTime(discharged) : null,
            },
        ServiceProvider = refs.Organization(s.FacilityId),
    };

    private static R4.ResourceReference PatientReference(DischargeSummarySource source) => AbdmParticipants.Patient(source.Patient);

    /// <summary>"27 Oct 2026: Review in the OPD", the date or the instructions alone, or null.</summary>
    private static string? FollowUp(DischargeSummaryRow s)
    {
        var instructions = FhirElements.Clean(s.FollowUpInstructions);
        return s.FollowUpDate is { } date
            ? instructions is null ? DocumentFormat.Date(date) : $"{DocumentFormat.Date(date)}: {instructions}"
            : instructions;
    }

    private static void Section(
        List<R4.Composition.SectionComponent> sections, SectionCode code, string title, SectionText text, params R4.ResourceReference[] entries)
    {
        if (text.IsEmpty && entries.Length == 0)
        {
            return;
        }

        sections.Add(new R4.Composition.SectionComponent
        {
            Title = title,
            Code = code.ToConcept(),
            Text = text.ToNarrative(),
            Entry = [.. entries],
        });
    }
}
