using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Clinical diagnoses → FHIR <c>Condition</c>, coded when a code was chosen from the catalogue (free-text working
/// diagnoses are sent as text): WHO ICD-10 (<c>http://hl7.org/fhir/sid/icd-10</c>) or ICD-11 MMS
/// (<c>http://id.who.int/icd/release/11/mms</c>). The system follows from the code's form, which WHO made distinct
/// (<see cref="CodeReleases.OfDiagnosisCode"/>), so a diagnosis needs no stored release; a code of neither form is sent
/// as text only rather than under the wrong system. A diagnosis made in an encounter is an <c>encounter-diagnosis</c>;
/// active and chronic non-differential diagnoses also form the <c>problem-list-item</c>s.
/// </summary>
internal static class ConditionMapper
{
    public const string ProblemListItem = "problem-list-item";
    public const string EncounterDiagnosis = "encounter-diagnosis";

    public static R4.Condition Map(DiagnosisRow d)
    {
        var condition = new R4.Condition
        {
            Id = FhirIds.Of(d.Id),
            Meta = FhirElements.Meta(d.CreatedAt, d.UpdatedAt),
            ClinicalStatus = ClinicalStatus(d.Status),
            VerificationStatus = VerificationStatus(d.Type),
            Code = Code(d.IcdCode, d.Title),
            Subject = FhirReferences.Patient(d.PatientId),
            Encounter = d.EncounterId is { } encounter ? FhirReferences.Encounter(FhirIds.ClinicalEncounter, encounter) : null,
            Onset = d.OnsetDate is { } onset ? new R4.FhirDateTime(FhirTime.Date(onset)) : null,
            Abatement = d.Status == DiagnosisStatus.Resolved && d.ResolvedOn is { } resolved ? new R4.FhirDateTime(FhirTime.Date(resolved)) : null,
            RecordedDate = FhirTime.DateTime(d.RecordedAt),
        };

        condition.Category.AddRange(Categories(d.Type, d.Status, d.EncounterId is not null));
        if (FhirElements.Note(d.Notes) is { } note)
        {
            condition.Note.Add(note);
        }

        return condition;
    }

    /// <summary>Active and chronic diagnoses are active (chronic says so in the text); resolved ones are resolved.</summary>
    public static R4.CodeableConcept ClinicalStatus(DiagnosisStatus status) => status switch
    {
        DiagnosisStatus.Resolved => FhirElements.Concept(FhirSystems.ConditionClinical, "resolved", "Resolved"),
        DiagnosisStatus.Chronic => FhirElements.Concept(FhirSystems.ConditionClinical, "active", "Active", "Chronic"),
        _ => FhirElements.Concept(FhirSystems.ConditionClinical, "active", "Active"),
    };

    public static R4.CodeableConcept VerificationStatus(DiagnosisType type) => type switch
    {
        DiagnosisType.Final => FhirElements.Concept(FhirSystems.ConditionVerification, "confirmed", "Confirmed"),
        DiagnosisType.Differential => FhirElements.Concept(FhirSystems.ConditionVerification, "differential", "Differential"),
        _ => FhirElements.Concept(FhirSystems.ConditionVerification, "provisional", "Provisional"),
    };

    public static IEnumerable<R4.CodeableConcept> Categories(DiagnosisType type, DiagnosisStatus status, bool madeInEncounter)
    {
        var onProblemList = type != DiagnosisType.Differential && (status is DiagnosisStatus.Active or DiagnosisStatus.Chronic || !madeInEncounter);
        if (onProblemList)
        {
            yield return FhirElements.Concept(FhirSystems.ConditionCategory, ProblemListItem, "Problem List Item");
        }

        if (madeInEncounter || !onProblemList)
        {
            yield return FhirElements.Concept(FhirSystems.ConditionCategory, EncounterDiagnosis, "Encounter Diagnosis");
        }
    }

    public static R4.CodeableConcept Code(string? icdCode, string title)
    {
        var code = FhirElements.Clean(icdCode)?.ToUpperInvariant();
        var text = FhirElements.Clean(title) ?? code ?? "Diagnosis";
        var concept = new R4.CodeableConcept { Text = text };
        var system = CodeReleases.OfDiagnosisCode(code) switch
        {
            CodeReleases.Icd10 => FhirSystems.Icd10,
            CodeReleases.Icd11 => FhirSystems.Icd11Mms,
            _ => null,
        };
        if (code is not null && system is not null)
        {
            concept.Coding.Add(FhirElements.Coding(system, code, FhirElements.Clean(title)));
        }

        return concept;
    }
}
