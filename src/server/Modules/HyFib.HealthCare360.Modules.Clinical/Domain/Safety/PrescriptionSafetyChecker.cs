namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>
/// Runs every prescribing safety check (ERX-02/06) as a pure function of the prescription, the patient's allergies and
/// current medications and the configurable reference tables. Nothing is filtered out: every finding is returned,
/// blocking ones first, so the prescriber always sees the complete picture.
/// </summary>
public static class PrescriptionSafetyChecker
{
    public static SafetyCheckResult Check(SafetyCheckInput input)
    {
        var alerts = new List<SafetyAlert>();
        alerts.AddRange(AllergyCheck.Run(input.Items, input.Allergies));
        alerts.AddRange(InteractionCheck.Run(input.Items, input.CurrentMedications, input.Interactions));
        alerts.AddRange(DuplicateTherapyCheck.Run(input.Items, input.CurrentMedications));
        alerts.AddRange(DoseCheck.Run(input.Items, input.CurrentMedications, input.DoseLimits));
        alerts.AddRange(RouteCheck.Run(input.Items));
        alerts.AddRange(input.Items.Where(i => i.IsControlledDrug).Select(i => new SafetyAlert(
            $"controlled:{i.Id}", SafetyAlertKind.ControlledDrug, SafetyAlertSeverity.Info, IsBlocking: false,
            $"{i.DrugName} is a Schedule H1/X (narcotic/psychotropic) medicine: confirm it explicitly; it is entered in the controlled-drug register.",
            null, [i.Id])));

        return new SafetyCheckResult(alerts
            .DistinctBy(a => a.Key)
            .OrderByDescending(a => a.IsBlocking)
            .ThenByDescending(a => a.Severity)
            .ThenBy(a => a.Kind)
            .ToList());
    }
}
