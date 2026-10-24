using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// What a doctor may prescribe in a teleconsultation (Telemedicine Practice Guidelines 2020, Board of Governors in
/// supersession of the MCI, 25 March 2020, section 3.7): never a medicine of the Prohibited List, which is any medicine in
/// Schedule X of the Drugs and Cosmetics Rules and any narcotic or psychotropic substance under the NDPS Act. The patient
/// is seen in person for those.
/// </summary>
public static class TelemedicinePrescribing
{
    /// <summary>A medicine on the prescription as the drug master has it.</summary>
    public sealed record Medicine(string Name, DrugSchedule Schedule, bool IsNarcoticOrPsychotropic);

    /// <summary>The medicines on the Prohibited List, or none.</summary>
    public static IReadOnlyList<string> Prohibited(IEnumerable<Medicine> medicines) =>
        [.. medicines.Where(m => m.Schedule == DrugSchedule.X || m.IsNarcoticOrPsychotropic).Select(m => m.Name).Distinct()];

    public static Error Refusal(IReadOnlyList<string> prohibited) => Error.Rule("prescription.telemedicine_prohibited",
        $"{string.Join(", ", prohibited)} cannot be prescribed in a teleconsultation: Schedule X and NDPS medicines are on the "
        + "Telemedicine Practice Guidelines' Prohibited List. See the patient in person to prescribe them.");
}
