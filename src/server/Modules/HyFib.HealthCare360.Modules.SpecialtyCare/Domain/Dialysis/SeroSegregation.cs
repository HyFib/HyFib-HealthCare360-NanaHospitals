namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

/// <summary>
/// Infection-control rules for haemodialysis (DIA-02). Hepatitis B, hepatitis C and HIV spread between patients
/// through shared machines, so every machine is dedicated to one sero category and a patient may only be dialysed
/// on a machine of exactly their category. Enforced when a session is scheduled and again when it starts.
/// </summary>
public static class SeroSegregation
{
    /// <summary>Sero-negative patients are re-screened every 3 months (common Indian unit policy); older results are flagged.</summary>
    public const int RescreeningIntervalDays = 90;

    public static bool IsCompatible(SeroStatus patient, SeroStatus machineDedication) => patient == machineDedication;

    public static Result EnsureCompatible(SeroStatus patient, SeroStatus machineDedication, string machineCode) =>
        IsCompatible(patient, machineDedication)
            ? Result.Success()
            : Error.Rule(
                "dialysis.sero_mismatch",
                $"Machine {machineCode} is dedicated to {Describe(machineDedication)} patients; this patient is {Describe(patient)}. Use a machine of the same sero category.");

    /// <summary>HBsAg-positive patients must not take part in dialyzer reuse (CDC / ISN guidance): single-use only.</summary>
    public static bool AllowsDialyzerReuse(SeroStatus patient) => patient != SeroStatus.HBsAg;

    public static bool IsRescreeningDue(DateOnly? testedOn, DateOnly today) =>
        testedOn is not { } tested || today.DayNumber - tested.DayNumber > RescreeningIntervalDays;

    public static string Describe(SeroStatus status) => status switch
    {
        SeroStatus.Negative => "sero-negative",
        SeroStatus.HBsAg => "HBsAg-positive",
        SeroStatus.HCV => "HCV-positive",
        _ => "HIV-positive",
    };
}
