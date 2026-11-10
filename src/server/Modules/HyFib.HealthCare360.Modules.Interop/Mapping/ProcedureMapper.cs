using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Platform.Contracts;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Mapping;

/// <summary>
/// Clinical procedures (<see cref="ProcedureRecord"/>) → FHIR <c>Procedure</c>, as the ABDM guide's Procedure profile
/// reads them. The guide fixes the system of every coding of a procedure (its code, body site, outcome, category) to
/// SNOMED CT. HC360 codes a procedure in ICD-10-PCS, the procedure catalogue's, or names it in words, so a procedure goes
/// by its name, never as an ICD-10-PCS code under SNOMED CT's system (as <see cref="ConditionMapper"/> sends a diagnosis
/// whose code system it does not know). Body site and outcome go as written, the side included. An operation the
/// theatre completed is a surgical procedure. A record made by mistake goes as <c>entered-in-error</c> (searches leave it
/// out unless asked for), so a provider that read it learns it is wrong; a corrected one is completed, and says why it
/// was corrected.
/// </summary>
internal static class ProcedureMapper
{
    /// <summary>SNOMED CT 387713003 "Surgical procedure", the category of an operation (FHIR's procedure-category values).</summary>
    public static readonly (string Code, string Display) SurgicalProcedure = ("387713003", "Surgical procedure");

    public static R4.Procedure Map(ProcedureRow p, ReferenceDirectory refs)
    {
        var procedure = new R4.Procedure
        {
            Id = FhirIds.Of(p.Id),
            Meta = FhirElements.Meta(p.CreatedAt, p.UpdatedAt),
            Status = p.Status == ProcedureStatus.EnteredInError ? R4.EventStatus.EnteredInError : R4.EventStatus.Completed,
            Category = p.Source == ProcedureSource.OperationTheatre
                ? FhirElements.Concept(FhirSystems.Snomed, SurgicalProcedure.Code, SurgicalProcedure.Display)
                : null,
            Code = FhirElements.Text(FhirElements.Clean(p.Display) ?? "Procedure"),
            Subject = FhirReferences.Patient(p.PatientId),
            Encounter = FhirReferences.Context(p.EncounterId, p.VisitId, p.AdmissionId, refs),
            Performed = new R4.FhirDateTime(FhirTime.DateTime(p.PerformedAt)),
            Outcome = FhirElements.Clean(p.Outcome) is { } outcome ? FhirElements.Text(outcome) : null,
        };

        if (BodySite(p.Side, p.BodySite) is { } site)
        {
            procedure.BodySite.Add(site);
        }

        if (Performer(p, refs) is { } performer)
        {
            procedure.Performer.Add(performer);
        }

        foreach (var note in new[] { FhirElements.Clean(p.Notes), StatusNote(p) })
        {
            if (FhirElements.Note(note) is { } annotation)
            {
                procedure.Note.Add(annotation);
            }
        }

        return procedure;
    }

    /// <summary>
    /// The body site as written, with the side the record names ("Knee, left"; "Left side" when no site was written).
    /// Text only: the guide fixes the body site's code system to SNOMED CT, which the record does not hold.
    /// </summary>
    public static R4.CodeableConcept? BodySite(ProcedureSide side, string? recorded)
    {
        var site = FhirElements.Clean(recorded);
        var (alone, after) = side switch
        {
            ProcedureSide.Left => ("Left side", "left"),
            ProcedureSide.Right => ("Right side", "right"),
            ProcedureSide.Bilateral => ("Both sides", "both sides"),
            _ => ((string?)null, (string?)null),
        };

        return (site, alone) switch
        {
            (null, null) => null,
            (null, { } sideOnly) => FhirElements.Text(sideOnly),
            ({ } written, null) => FhirElements.Text(written),
            ({ } written, _) => FhirElements.Text($"{written}, {after}"),
        };
    }

    /// <summary>The performer: the practitioner (with their HPR ID) when there is a record of them here, else the name written.</summary>
    private static R4.Procedure.PerformerComponent? Performer(ProcedureRow p, ReferenceDirectory refs)
    {
        var actor = refs.Practitioner(p.PerformerPractitionerId)
            ?? (FhirElements.Clean(p.PerformerName) is { } name ? new R4.ResourceReference { Display = name } : null);
        return actor is null ? null : new R4.Procedure.PerformerComponent { Actor = actor };
    }

    /// <summary>Why a record was corrected, or marked as entered in error.</summary>
    private static string? StatusNote(ProcedureRow p) => p.Status switch
    {
        ProcedureStatus.EnteredInError => $"Entered in error: {FhirElements.Clean(p.StatusReason) ?? "no reason given"}",
        ProcedureStatus.Amended => $"Corrected: {FhirElements.Clean(p.StatusReason) ?? "no reason given"}",
        _ => null,
    };
}
