using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Documents → FHIR <c>DocumentReference</c>:
/// <list type="bullet">
/// <item>documents attached to the patient record (<c>doc-</c>), typed with LOINC document codes where one fits. The
/// metadata (type, date, title, source, content type and size) is shared; the encrypted file itself is not served by
/// this facade. Identity proofs and insurance cards are not clinical records and are never shared;</item>
/// <item>signed discharge summaries (<c>ds-</c>, LOINC 18842-5) with the whole summary embedded as UTF-8 text.</item>
/// </list>
/// </summary>
internal static class DocumentReferenceMapper
{
    public const string DischargeSummaryLoinc = "18842-5";

    /// <summary>Only clinical documents leave the hospital; identity and insurance documents stay.</summary>
    public static bool IsShared(PatientDocumentType type) =>
        type is not (PatientDocumentType.IdProof or PatientDocumentType.InsuranceCard);

    public static R4.DocumentReference FromPatientDocument(PatientDocumentRow d)
    {
        var document = new R4.DocumentReference
        {
            Id = FhirIds.Of(FhirIds.PatientDocument, d.Id),
            Meta = FhirElements.Meta(d.CreatedAt, d.UpdatedAt),
            Status = R4.DocumentReferenceStatus.Current,
            Type = Type(d.Type),
            Subject = FhirReferences.Patient(d.PatientId),
            Date = FhirTime.ToMilliseconds(d.CreatedAt),
            Description = FhirElements.Clean(d.Title),
        };

        if (FhirElements.Clean(d.Source) is { } source)
        {
            document.Author.Add(new R4.ResourceReference { Display = source });
        }

        document.Content.Add(new R4.DocumentReference.ContentComponent
        {
            Attachment = new R4.Attachment
            {
                ContentType = FhirElements.Clean(d.ContentType) ?? "application/octet-stream",
                Title = FhirElements.Clean(d.Title),
                Creation = d.DocumentDate is { } date ? FhirTime.Date(date) : FhirTime.DateTime(d.CreatedAt),
                SizeUnsignedInt = d.SizeBytes is { } size and >= 0 and <= int.MaxValue ? (int)size : null,
            },
        });
        return document;
    }

    public static R4.DocumentReference FromDischargeSummary(DischargeSummaryRow s, ReferenceDirectory refs)
    {
        var signedBy = refs.Practitioner(s.SignedByPractitionerId ?? s.PractitionerId);
        var document = new R4.DocumentReference
        {
            Id = FhirIds.Of(FhirIds.DischargeSummary, s.Id),
            Meta = FhirElements.Meta(s.CreatedAt, s.UpdatedAt),
            Status = R4.DocumentReferenceStatus.Current,
            DocStatus = s.Status == DischargeSummaryStatus.Amended ? R4.CompositionStatus.Amended : R4.CompositionStatus.Final,
            Type = FhirElements.Concept(FhirSystems.Loinc, DischargeSummaryLoinc, "Discharge summary", "Discharge summary"),
            Subject = FhirReferences.Patient(s.PatientId),
            Date = FhirTime.ToMilliseconds(s.AmendedAt ?? s.SignedAt ?? s.CreatedAt),
            Description = "Discharge summary",
            Authenticator = signedBy,
            Context = new R4.DocumentReference.ContextComponent
            {
                Period = s.AdmittedAt is null && s.DischargedAt is null
                    ? null
                    : new R4.Period
                    {
                        Start = s.AdmittedAt is { } admitted ? FhirTime.DateTime(admitted) : null,
                        End = s.DischargedAt is { } discharged ? FhirTime.DateTime(discharged) : null,
                    },
            },
        };

        if (signedBy is not null)
        {
            document.Author.Add(signedBy);
        }

        var stay = refs.AdmissionEncounter(s.AdmissionId)
                   ?? (s.EncounterId is { } encounter ? FhirReferences.Encounter(FhirIds.ClinicalEncounter, encounter) : null);
        if (stay is not null)
        {
            document.Context.Encounter.Add(stay);
        }

        document.Content.Add(new R4.DocumentReference.ContentComponent
        {
            Attachment = DiagnosticReportMapper.TextAttachment(DischargeSummaryText(s), "Discharge summary", s.AmendedAt ?? s.SignedAt),
        });
        return document;
    }

    /// <summary>Document type: LOINC for discharge summaries, referral notes, consents, prescriptions, imaging and photographs.</summary>
    public static R4.CodeableConcept Type(PatientDocumentType type) => type switch
    {
        PatientDocumentType.DischargeSummary => FhirElements.Concept(FhirSystems.Loinc, DischargeSummaryLoinc, "Discharge summary", "Discharge summary"),
        PatientDocumentType.ReferralLetter => FhirElements.Concept(FhirSystems.Loinc, "57133-1", "Referral note", "Referral letter"),
        PatientDocumentType.Consent => FhirElements.Concept(FhirSystems.Loinc, "59284-0", "Patient Consent", "Consent form"),
        PatientDocumentType.Prescription => FhirElements.Concept(FhirSystems.Loinc, "57833-6", "Prescription for medication", "Prescription"),
        PatientDocumentType.Imaging => FhirElements.Concept(FhirSystems.Loinc, "18748-4", "Diagnostic imaging study", "Imaging"),
        PatientDocumentType.Photo => FhirElements.Concept(FhirSystems.Loinc, "72170-4", "Photographic image", "Clinical photograph"),
        PatientDocumentType.ExternalReport => FhirElements.Text("External report"),
        PatientDocumentType.IdProof => FhirElements.Text("Identity proof"),
        PatientDocumentType.InsuranceCard => FhirElements.Text("Insurance card"),
        _ => FhirElements.Text("Other document"),
    };

    public static string DischargeSummaryText(DischargeSummaryRow s)
    {
        var text = new ReportText();
        text.Line("Discharge summary");
        text.Field("Admitted", s.AdmittedAt is { } admitted ? FhirTime.DateTime(admitted) : null);
        text.Field("Discharged", s.DischargedAt is { } discharged ? FhirTime.DateTime(discharged) : null);
        text.Field("Diagnoses", s.Diagnoses);
        text.Field("ICD-10 codes", s.DiagnosisCodes.Count > 0 ? string.Join(", ", s.DiagnosisCodes) : null);
        text.Field("Presenting complaints", s.PresentingComplaints);
        text.Field("Course in hospital", s.CourseInHospital);
        text.Field("Procedures", s.Procedures);
        text.Field("Investigations", s.InvestigationsSummary);
        text.Field("Condition at discharge", s.ConditionAtDischarge);
        text.Field("Medications on discharge", s.Medications);
        text.Field("Advice", s.Advice);
        text.Field("Follow-up date", s.FollowUpDate is { } followUp ? FhirTime.Date(followUp) : null);
        text.Field("Follow-up instructions", s.FollowUpInstructions);
        if (s.Status == DischargeSummaryStatus.Amended && s.AmendedAt is { } amended)
        {
            text.Line($"Amended on {FhirTime.DateTime(amended)}.");
        }

        return text.ToString();
    }
}
