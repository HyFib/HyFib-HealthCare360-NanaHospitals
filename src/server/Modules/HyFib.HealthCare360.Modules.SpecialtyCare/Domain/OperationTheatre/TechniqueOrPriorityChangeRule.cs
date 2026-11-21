namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>
/// Who may change the anaesthesia technique or the priority once the WHO Sign-in is done, as with the side or procedure
/// (<see cref="SiteChangeRule"/>): only the operating surgeon or the anaesthetist of the case, from their own sign-in, with a
/// reason; the caller audits the change at Warning (<c>surgery.plan_changed_after_sign_in</c>). Both bear on the patient's
/// consent: local or topical anaesthesia needs no consent to anaesthesia (one needed at the Sign-in stays needed,
/// <see cref="SurgeryBooking.AnaesthesiaConsentRequiredAtSignIn"/>), and only an emergency goes ahead without consent
/// (<see cref="SurgeryBooking.WaiveConsent"/>). Before the Sign-in both are edited as usual; once the operation has started
/// the plan is not changed (<see cref="SurgeryBooking.CanChangePlan"/>).
/// </summary>
public static class TechniqueOrPriorityChangeRule
{
    public static Result Check(
        bool signedIn,
        AnaesthesiaType anaesthesiaBefore,
        AnaesthesiaType anaesthesiaAsked,
        SurgeryPriority priorityBefore,
        SurgeryPriority priorityAsked,
        Guid? surgeonId,
        Guid? anaesthetistId,
        Guid? byPractitionerId,
        string? reason)
    {
        if (!signedIn || (anaesthesiaBefore == anaesthesiaAsked && priorityBefore == priorityAsked))
        {
            return Result.Success();
        }

        if (byPractitionerId is not { } by || (by != surgeonId && by != anaesthetistId))
        {
            return Error.Forbidden("surgery.plan_change_team",
                "After the WHO Sign-in, only the operating surgeon or the anaesthetist of this case changes the anaesthesia technique or the priority, from their own sign-in.");
        }

        return string.IsNullOrWhiteSpace(reason)
            ? Error.Field("changeReason", "Give the reason for changing the anaesthesia technique or the priority after the WHO Sign-in.")
            : Result.Success();
    }

    /// <summary>"anaesthesia Spinal → Local; priority Elective → Emergency": what changed, for the audit trail.</summary>
    public static string Describe(
        AnaesthesiaType anaesthesiaBefore, AnaesthesiaType anaesthesiaAfter, SurgeryPriority priorityBefore, SurgeryPriority priorityAfter)
    {
        var parts = new List<string>();
        if (anaesthesiaBefore != anaesthesiaAfter)
        {
            parts.Add($"anaesthesia {anaesthesiaBefore} → {anaesthesiaAfter}");
        }

        if (priorityBefore != priorityAfter)
        {
            parts.Add($"priority {priorityBefore} → {priorityAfter}");
        }

        return string.Join("; ", parts);
    }
}
