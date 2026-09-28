using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Clinical allergies → FHIR <c>AllergyIntolerance</c>. The allergen is free text (generic drug, drug class or food).
/// Severe and anaphylactic allergies are high criticality; an anaphylaxis is also stated as a SNOMED CT manifestation.
/// An allergy recorded by mistake is sent as <c>entered-in-error</c> without a clinical status, as FHIR requires.
/// </summary>
internal static class AllergyIntoleranceMapper
{
    public const string AnaphylaxisSnomed = "39579001";

    public static R4.AllergyIntolerance Map(AllergyRow a)
    {
        var enteredInError = a.Status == AllergyStatus.EnteredInError;
        var allergy = new R4.AllergyIntolerance
        {
            Id = FhirIds.Of(a.Id),
            Meta = FhirElements.Meta(a.CreatedAt, a.UpdatedAt),
            ClinicalStatus = enteredInError ? null : ClinicalStatus(a.Status),
            VerificationStatus = enteredInError
                ? FhirElements.Concept(FhirSystems.AllergyVerification, "entered-in-error", "Entered in Error")
                : null,
            Criticality = a.Severity is AllergySeverity.Severe or AllergySeverity.Anaphylaxis
                ? R4.AllergyIntolerance.AllergyIntoleranceCriticality.High
                : R4.AllergyIntolerance.AllergyIntoleranceCriticality.Low,
            Code = FhirElements.Text(FhirElements.Clean(a.Allergen) ?? "Unknown allergen"),
            Patient = FhirReferences.Patient(a.PatientId),
            Onset = a.OnsetDate is { } onset ? new R4.FhirDateTime(FhirTime.Date(onset)) : null,
            RecordedDate = FhirTime.DateTime(a.RecordedAt),
        };

        if (Category(a.Type) is { } category)
        {
            allergy.Category = [category];
        }

        if (Reaction(a.Reaction, a.Severity) is { } reaction)
        {
            allergy.Reaction.Add(reaction);
        }

        foreach (var note in new[] { FhirElements.Clean(a.Notes), FhirElements.Clean(a.StatusReason) is { } reason ? $"Status reason: {reason}" : null })
        {
            if (FhirElements.Note(note) is { } annotation)
            {
                allergy.Note.Add(annotation);
            }
        }

        return allergy;
    }

    public static R4.CodeableConcept ClinicalStatus(AllergyStatus status) => status switch
    {
        AllergyStatus.Inactive => FhirElements.Concept(FhirSystems.AllergyClinical, "inactive", "Inactive"),
        AllergyStatus.Resolved => FhirElements.Concept(FhirSystems.AllergyClinical, "resolved", "Resolved"),
        _ => FhirElements.Concept(FhirSystems.AllergyClinical, "active", "Active"),
    };

    public static R4.AllergyIntolerance.AllergyIntoleranceCategory? Category(AllergyType type) => type switch
    {
        AllergyType.Drug => R4.AllergyIntolerance.AllergyIntoleranceCategory.Medication,
        AllergyType.Food => R4.AllergyIntolerance.AllergyIntoleranceCategory.Food,
        AllergyType.Environmental => R4.AllergyIntolerance.AllergyIntoleranceCategory.Environment,
        _ => null,
    };

    public static R4.AllergyIntolerance.AllergyIntoleranceSeverity Severity(AllergySeverity severity) => severity switch
    {
        AllergySeverity.Mild => R4.AllergyIntolerance.AllergyIntoleranceSeverity.Mild,
        AllergySeverity.Moderate => R4.AllergyIntolerance.AllergyIntoleranceSeverity.Moderate,
        _ => R4.AllergyIntolerance.AllergyIntoleranceSeverity.Severe,
    };

    /// <summary>A reaction needs a manifestation: the recorded reaction text, and anaphylaxis when that was the severity.</summary>
    public static R4.AllergyIntolerance.ReactionComponent? Reaction(string? reactionText, AllergySeverity severity)
    {
        var text = FhirElements.Clean(reactionText);
        var anaphylaxis = severity == AllergySeverity.Anaphylaxis;
        if (text is null && !anaphylaxis)
        {
            return null;
        }

        var reaction = new R4.AllergyIntolerance.ReactionComponent { Severity = Severity(severity) };
        if (text is not null)
        {
            reaction.Manifestation.Add(FhirElements.Text(text));
        }

        if (anaphylaxis)
        {
            reaction.Manifestation.Add(FhirElements.Concept(FhirSystems.Snomed, AnaphylaxisSnomed, "Anaphylaxis", "Anaphylaxis"));
        }

        return reaction;
    }
}
