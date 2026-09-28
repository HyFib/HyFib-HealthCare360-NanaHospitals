using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>
/// Compatibility test of one donor unit against the patient's sample (BB-06). A compatible result reserves the
/// unit for the request; it stays valid for 72 hours because new antibodies may appear after recent transfusion.
/// </summary>
public sealed class CrossMatch : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(72);

    public Guid FacilityId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ComponentId { get; set; }
    public Guid PatientId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public CrossmatchMethod Method { get; set; }
    public CrossmatchResult Result { get; set; }
    public DateTimeOffset PerformedAt { get; set; }
    public string? PerformedByName { get; set; }
    public string? Remarks { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    public bool IsValidCompatibleAt(DateTimeOffset now) => Result == CrossmatchResult.Compatible && now - PerformedAt <= Validity;

    /// <summary>
    /// Guards against transcription errors: "compatible" is refused when the ABO/Rh table says the unit cannot be
    /// given to this patient (the group of either the unit or the patient must be wrong and needs rechecking).
    /// </summary>
    public static Result EnsureConsistent(CrossmatchResult result, BloodComponentType component, BloodGroup unitGroup, BloodGroup patientGroup)
    {
        if (!BloodCompatibility.IsKnown(patientGroup))
        {
            return Error.Rule("bloodbank.patient_not_grouped", "Group the patient's sample before cross-matching.");
        }

        return result == CrossmatchResult.Compatible && !BloodCompatibility.IsCompatible(component, unitGroup, patientGroup)
            ? Error.Rule("bloodbank.abo_incompatible",
                $"A {unitGroup} {component} unit is ABO/Rh incompatible with a {patientGroup} patient; recheck the groups before recording a compatible cross-match.")
            : SharedKernel.Result.Success();
    }

    public void RecordCharges(ChargeStatus status, string? error)
    {
        ChargeStatus = status;
        ChargeError = error;
    }
}
