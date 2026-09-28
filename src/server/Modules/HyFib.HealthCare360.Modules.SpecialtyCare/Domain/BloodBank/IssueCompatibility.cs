namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

/// <summary>Facts needed to decide whether a unit may be handed over for a patient.</summary>
public sealed record IssueContext(
    BloodComponentType Component,
    BloodGroup UnitGroup,
    BloodGroup PatientGroup,
    bool HasValidCompatibleCrossmatch,
    bool EmergencyReleaseAuthorised);

/// <summary>
/// Final bedside-safety gate at the blood bank counter (BB-06/07), checked server-side on every issue:
/// <list type="bullet">
/// <item>Red cells need a compatible cross-match done within 72 hours — or, in a documented emergency release,
/// only group O Rh(D)-negative packed red cells may go uncross-matched.</item>
/// <item>Plasma, platelets and cryoprecipitate need an ABO plasma-compatible unit for the grouped patient —
/// or, in an emergency release before grouping, group AB (universal plasma).</item>
/// </list>
/// Returns whether the issue happens under emergency release, so it is flagged on the issue record.
/// </summary>
public static class IssueCompatibility
{
    public static Result<bool> Check(IssueContext context)
    {
        if (BloodCompatibility.ContainsRedCells(context.Component))
        {
            if (context.HasValidCompatibleCrossmatch)
            {
                return BloodCompatibility.IsCompatible(context.Component, context.UnitGroup, context.PatientGroup)
                    ? Result<bool>.Ok(false)
                    : Error.Rule("bloodbank.abo_incompatible", "The unit is ABO/Rh incompatible with the patient's group.");
            }

            if (context.EmergencyReleaseAuthorised && context.Component == BloodComponentType.PRBC && context.UnitGroup == BloodGroup.ONegative)
            {
                return true;
            }

            return Error.Rule("bloodbank.crossmatch_required",
                context.EmergencyReleaseAuthorised
                    ? "Uncross-matched emergency release is limited to group O Rh(D)-negative packed red cells."
                    : "Red cells can only be issued against a compatible cross-match done in the last 72 hours.");
        }

        if (BloodCompatibility.IsKnown(context.PatientGroup))
        {
            return BloodCompatibility.PlasmaCompatible(context.UnitGroup, context.PatientGroup)
                ? Result<bool>.Ok(false)
                : Error.Rule("bloodbank.abo_incompatible", $"{context.UnitGroup} plasma is incompatible with a {context.PatientGroup} patient.");
        }

        if (context.EmergencyReleaseAuthorised && BloodCompatibility.Abo(context.UnitGroup) == AboGroup.AB)
        {
            return true;
        }

        return Error.Rule("bloodbank.patient_not_grouped", "Group the patient's sample before issuing plasma, platelets or cryoprecipitate.");
    }
}
