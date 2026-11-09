using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// What a doctor may prescribe in a teleconsultation (Telemedicine Practice Guidelines 2020, Board of Governors in
/// supersession of the MCI, 25 March 2020, section 3.7.4 and Annexure 2), by the list the medicine is on:
/// <list type="bullet">
/// <item>List O, in any teleconsultation;</item>
/// <item>List A, in a first teleconsultation only when it is by video, and re-prescribed in a follow-up;</item>
/// <item>List B, only added on in a follow-up, for the condition the doctor saw the patient in person for;</item>
/// <item>the Prohibited List (any medicine in Schedule X of the Drugs and Cosmetics Rules, and any narcotic or psychotropic
/// substance under the NDPS Act), never: the patient is seen in person for those.</item>
/// </list>
/// The pharmacy and therapeutics committee puts medicines on Lists O, A and B in the item master. A medicine on none yet, or
/// typed in rather than chosen from the drug master, is not prescribed in a first teleconsultation, and is in a follow-up
/// as List B is. A follow-up (section 3.4) is a teleconsultation with the doctor who saw the patient in person in the last
/// six months, for the same condition; anything else is a first teleconsultation.
/// </summary>
public static class TelemedicinePrescribing
{
    public const string ProhibitedCode = "prescription.telemedicine_prohibited";
    public const string UnlistedCode = "prescription.telemedicine_unlisted";
    public const string ListBNeedsFollowUpCode = "prescription.telemedicine_list_b_needs_follow_up";
    public const string ListANeedsVideoCode = "prescription.telemedicine_list_a_needs_video";

    /// <summary>How long after seeing the patient in person a teleconsultation with the same doctor is a follow-up (section 3.4).</summary>
    public const int FollowUpMonths = 6;

    /// <summary>A medicine on the prescription as the drug master has it; <paramref name="List"/> is null when it is on no list.</summary>
    public sealed record Medicine(string Name, DrugSchedule Schedule, bool IsNarcoticOrPsychotropic, TelemedicineList? List = null)
    {
        /// <summary>The list that decides: the Prohibited List for a Schedule X or NDPS medicine, whatever the item master says.</summary>
        public TelemedicineList? Effective => TelemedicineLists.ByLaw(Schedule, IsNarcoticOrPsychotropic) ? TelemedicineLists.Prohibited : List;
    }

    /// <summary>
    /// The teleconsultation: whether the doctor saw the patient by video in it, and whether it is a follow-up of an in-person
    /// consultation for the same condition.
    /// </summary>
    public sealed record Consult(bool ByVideo, bool IsFollowUp);

    /// <summary>A medicine the teleconsultation may not prescribe: its list ("O", "A", "B", "Prohibited" or "None"), the code and why.</summary>
    public sealed record RefusedMedicine(string Name, string List, string Code, string Reason);

    /// <summary>The medicines this teleconsultation may not prescribe, a medicine once; none when all may be.</summary>
    public static IReadOnlyList<RefusedMedicine> Refused(IEnumerable<Medicine> medicines, Consult consult)
    {
        ArgumentNullException.ThrowIfNull(medicines);
        ArgumentNullException.ThrowIfNull(consult);
        return [.. medicines.DistinctBy(m => m.Name).Select(m => Check(m, consult)).OfType<RefusedMedicine>()];
    }

    private static RefusedMedicine? Check(Medicine medicine, Consult consult)
    {
        var list = medicine.Effective;
        var name = medicine.Name;
        return list switch
        {
            TelemedicineList.O => null,
            TelemedicineList.A when consult.ByVideo || consult.IsFollowUp => null,
            TelemedicineList.A => new(name, "A", ListANeedsVideoCode,
                $"{name} cannot be prescribed in this teleconsultation: it is on List A, which a first teleconsultation prescribes only "
                + "by video, and this one was not held by video (no video call on the visit, or the call fell back to audio). "
                + "Prescribe it after seeing the patient by video, or in a follow-up."),
            TelemedicineList.B or null when consult.IsFollowUp => null,
            TelemedicineList.B => new(name, "B", ListBNeedsFollowUpCode,
                $"{name} cannot be prescribed in this teleconsultation: it is on List B, which is only added on in a follow-up, for a "
                + FollowUpWords + " See the patient in person to start it."),
            null => new(name, "None", UnlistedCode,
                $"{name} cannot be prescribed in a first teleconsultation: it is on no list of the Telemedicine Practice Guidelines yet "
                + "(the pharmacy and therapeutics committee puts medicines on Lists O, A and B in the item master), so it is prescribed "
                + "only in a follow-up, for a " + FollowUpWords),
            _ => new(name, "Prohibited", ProhibitedCode,
                $"{name} cannot be prescribed in a teleconsultation: Schedule X and NDPS medicines, and any the committee put there, "
                + "are on the Telemedicine Practice Guidelines' Prohibited List. See the patient in person to prescribe it."),
        };
    }

    private const string FollowUpWords =
        "condition you saw the patient in person for in the last six months; this teleconsultation is not one (record its "
        + "diagnosis: a diagnosis of the same ICD-10 category recorded when you saw the patient in person makes it a follow-up).";

    /// <summary>
    /// The refusal to sign: every medicine refused, and why. Its code is the gravest reason (the Prohibited List first); the
    /// screen lists each medicine from the problem's data.
    /// </summary>
    public static Error Refusal(IReadOnlyList<RefusedMedicine> refused, Consult consult)
    {
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(consult);
        string[] gravity = [ProhibitedCode, UnlistedCode, ListBNeedsFollowUpCode, ListANeedsVideoCode];
        var code = gravity.First(c => refused.Any(r => r.Code == c));
        return Error.Rule(code, string.Join(" ", refused.Select(r => r.Reason))) with
        {
            Data = new { telemedicine = refused, consult = new { consult.ByVideo, consult.IsFollowUp } },
        };
    }
}
