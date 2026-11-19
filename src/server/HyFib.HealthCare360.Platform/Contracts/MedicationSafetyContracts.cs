namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// A medicine on a patient's ward chart, or about to be put on it, as the ward writes it: the frequency in words ("BD",
/// "1-0-1", "Q6H", "SOS", "As directed"). <paramref name="DrugItemId"/> is the drug master's item when it was picked from it.
/// </summary>
public sealed record WardMedicine(
    Guid Id,
    Guid? DrugItemId,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? Route,
    string Dose,
    string Frequency);

/// <summary>
/// One prescribing safety finding (ERX-02), as an e-prescription's check reports it. <paramref name="Kind"/> and
/// <paramref name="Severity"/> are its names there (Allergy, ClassAllergy, CrossSensitivity, DuplicateTherapy, Interaction,
/// MaxDose, DoseUnverified, HighAlertMedication, Route; Info, Minor, Moderate, Major). A blocking one stops the order unless
/// a clinical reason is given for going ahead.
/// </summary>
public sealed record MedicationSafetyAlert(string Key, string Kind, string Severity, bool IsBlocking, string Message, string? Detail);

/// <summary>What the prescribing safety check found for a medicine, blocking alerts first.</summary>
public sealed record MedicationSafetyResult(IReadOnlyList<MedicationSafetyAlert> Alerts)
{
    public bool IsBlocked => Alerts.Any(a => a.IsBlocking);
}

/// <summary>
/// The prescribing safety check, kept by Clinical, for a medicine another module puts on a patient's chart without an
/// e-prescription: a ward's medication order, the doctor's own or a verbal or telephone one. It finds what an e-prescription
/// line of that medicine would raise at signing: the patient's active allergies, interactions and duplicate therapy with
/// their current medicines (signed prescriptions and the ward's own orders, <see cref="IWardMedicines"/>), the maximum
/// daily dose and the route.
/// </summary>
public interface IMedicationSafetyCheck
{
    Task<MedicationSafetyResult> CheckAsync(Guid patientId, WardMedicine medicine, CancellationToken cancellationToken);
}

/// <summary>
/// The medicines a patient is having on the ward that no signed e-prescription carries, kept by Inpatient: active ward
/// orders (the doctor's own, verbal or telephone) and medicines the pharmacy issued without a prescription line, of a stay
/// still in its bed and not ended. The prescribing safety check counts them among the patient's current medicines.
/// </summary>
public interface IWardMedicines
{
    Task<IReadOnlyList<WardMedicine>> ActiveAsync(Guid patientId, CancellationToken cancellationToken);
}
