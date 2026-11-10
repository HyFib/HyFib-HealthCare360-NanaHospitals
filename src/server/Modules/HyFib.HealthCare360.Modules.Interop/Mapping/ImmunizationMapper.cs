using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Clinical immunisations → FHIR <c>Immunization</c>. The vaccine is coded only from the card code staff chose
/// (<see cref="VaccineCodes"/>: a SNOMED CT product of the ABDM guide's vaccine codes), else sent by its name alone; the
/// route is coded only when it is one HC360 knows. A dose copied from the patient's card is not this hospital's own
/// record of giving it: <c>primarySource</c> false, its origin a written record. A dose recorded by mistake is sent as
/// <c>entered-in-error</c> (searches leave it out unless asked for), so a provider that read it learns it is wrong.
/// </summary>
internal static class ImmunizationMapper
{
    public static R4.Immunization Map(ImmunizationRow i)
    {
        var immunization = new R4.Immunization
        {
            Id = FhirIds.Of(i.Id),
            Meta = FhirElements.Meta(i.CreatedAt, i.UpdatedAt),
            Status = i.IsEnteredInError ? R4.Immunization.ImmunizationStatusCodes.EnteredInError : R4.Immunization.ImmunizationStatusCodes.Completed,
            VaccineCode = Vaccine(i.VaccineName, i.VaccineCode),
            Patient = FhirReferences.Patient(i.PatientId),
            Encounter = i.EncounterId is { } encounter ? FhirReferences.Encounter(FhirIds.ClinicalEncounter, encounter) : null,
            Occurrence = new R4.FhirDateTime(FhirTime.Date(i.AdministeredOn)),
            Recorded = FhirTime.DateTime(i.CreatedAt),
            PrimarySource = !i.IsExternal,
            ReportOrigin = i.IsExternal ? FhirElements.Concept(FhirSystems.ImmunizationOrigin, "record", "Written Record") : null,
            Manufacturer = FhirElements.Clean(i.Manufacturer) is { } manufacturer ? new R4.ResourceReference { Display = manufacturer } : null,
            LotNumber = FhirElements.Clean(i.BatchNumber),
            ExpirationDate = i.ExpiryDate is { } expiry ? FhirTime.Date(expiry) : null,
            Site = FhirElements.Clean(i.Site) is { } site ? FhirElements.Text(site) : null,
            Route = Route(i.Route),
        };

        if (Performer(i) is { } performer)
        {
            immunization.Performer.Add(performer);
        }

        if (i.DoseNumber is int dose && dose > 0)
        {
            immunization.ProtocolApplied.Add(new R4.Immunization.ProtocolAppliedComponent { DoseNumber = new R4.PositiveInt(dose) });
        }

        foreach (var note in new[]
                 {
                     FhirElements.Clean(i.Notes),
                     i.IsEnteredInError ? $"Entered in error: {FhirElements.Clean(i.EnteredInErrorReason) ?? "no reason given"}" : null,
                 })
        {
            if (FhirElements.Note(note) is { } annotation)
            {
                immunization.Note.Add(annotation);
            }
        }

        return immunization;
    }

    /// <summary>The vaccine: its SNOMED CT product when the card code is one HC360 knows, and always the name staff recorded.</summary>
    public static R4.CodeableConcept Vaccine(string name, string? cardCode)
    {
        var text = FhirElements.Clean(name) ?? "Vaccine";
        return VaccineCodes.Find(cardCode) is { } vaccine
            ? FhirElements.Concept(FhirSystems.Snomed, vaccine.Snomed, vaccine.SnomedDisplay, text)
            : FhirElements.Text(text);
    }

    /// <summary>
    /// The route as recorded: coded when it is intradermal, intramuscular, subcutaneous or oral in any of the usual
    /// spellings, else the words as recorded. The ABDM guide's Immunization profile fixes the route's code system to
    /// SNOMED CT, and its route value set is SNOMED CT's route of administration values (descendants of 284009009).
    /// </summary>
    public static R4.CodeableConcept? Route(string? recorded)
    {
        if (FhirElements.Clean(recorded) is not { } text)
        {
            return null;
        }

        return RouteOf(text) switch
        {
            VaccineRoute.Intradermal => FhirElements.Concept(FhirSystems.Snomed, "372464004", "Intradermal route", text),
            VaccineRoute.Intramuscular => FhirElements.Concept(FhirSystems.Snomed, "78421000", "Intramuscular route", text),
            VaccineRoute.Subcutaneous => FhirElements.Concept(FhirSystems.Snomed, "34206005", "Subcutaneous route", text),
            VaccineRoute.Oral => FhirElements.Concept(FhirSystems.Snomed, "26643006", "Oral route", text),
            _ => FhirElements.Text(text),
        };
    }

    /// <summary>The route a recorded text names: "IM", "Intramuscular", "s.c.", "Sub-cutaneous", "ID", "PO", "Oral drops"…</summary>
    public static VaccineRoute? RouteOf(string text)
    {
        var letters = string.Concat(text.ToUpperInvariant().Where(char.IsAsciiLetter));
        return letters switch
        {
            "ID" or "INTRADERMAL" or "INTRADERMALLY" => VaccineRoute.Intradermal,
            "IM" or "INTRAMUSCULAR" or "INTRAMUSCULARLY" => VaccineRoute.Intramuscular,
            "SC" or "SQ" or "SUBCUT" or "SUBCUTANEOUS" or "SUBCUTANEOUSLY" => VaccineRoute.Subcutaneous,
            "PO" or "ORAL" or "ORALLY" or "ORALDROPS" or "BYMOUTH" => VaccineRoute.Oral,
            _ => null,
        };
    }

    /// <summary>
    /// Who gave the dose, as the administering provider: the account that recorded a dose given here, else the vaccinator
    /// or centre written for a dose given elsewhere. Named only: an account or a card names no practitioner record.
    /// </summary>
    private static R4.Immunization.PerformerComponent? Performer(ImmunizationRow i)
    {
        var name = i.IsExternal ? FhirElements.Clean(i.AdministeredBy) : FhirElements.Clean(i.AdministeredByUserName) ?? FhirElements.Clean(i.AdministeredBy);
        return name is null
            ? null
            : new R4.Immunization.PerformerComponent
            {
                Function = FhirElements.Concept(FhirSystems.ImmunizationFunction, "AP", "Administering Provider"),
                Actor = new R4.ResourceReference { Display = name },
            };
    }
}
