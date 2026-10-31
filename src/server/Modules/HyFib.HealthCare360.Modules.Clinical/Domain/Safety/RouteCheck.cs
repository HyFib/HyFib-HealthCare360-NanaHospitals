using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>What a dosage form says about how a medicine is given, as far as it leaves no doubt.</summary>
public enum DosageFormKind
{
    /// <summary>The form does not settle the route (a solution, drops, an unknown form, none).</summary>
    Unknown = 0,

    /// <summary>An injection or an infusion: IV, IM, SC or another route by needle, never by mouth.</summary>
    Injection,

    /// <summary>Taken by mouth: a tablet, capsule, syrup, suspension, sachet…</summary>
    Oral,

    /// <summary>Put where it acts: skin, eye, ear, nose, lungs, rectum, vagina, under the tongue.</summary>
    Local,
}

/// <summary>
/// Reads a line's dosage form (the drug master's, free text such as "Powder for injection" or "Tablet (enteric-coated)"),
/// or, when the line has none, its name ("Inj. Ceftriaxone 1 g", "Paracetamol 500 mg Tablet").
/// </summary>
public static partial class DosageForms
{
    public static DosageFormKind Classify(string? dosageForm, string? drugName = null)
    {
        var text = string.IsNullOrWhiteSpace(dosageForm) ? drugName : dosageForm;
        if (string.IsNullOrWhiteSpace(text))
        {
            return DosageFormKind.Unknown;
        }

        if (Injection().IsMatch(text))
        {
            return DosageFormKind.Injection;
        }

        // Before oral: a nebuliser suspension is inhaled, an oral gel is put on the gums.
        if (Local().IsMatch(text))
        {
            return DosageFormKind.Local;
        }

        return Oral().IsMatch(text) ? DosageFormKind.Oral : DosageFormKind.Unknown;
    }

    /// <summary>A route given by needle: IV, IM, SC, intradermal.</summary>
    public static bool IsInjectionRoute(string? route) => route?.Trim().ToUpperInvariant() is "IV" or "IM" or "SC" or "ID"
        or "INTRAVENOUS" or "INTRAMUSCULAR" or "SUBCUTANEOUS" or "INTRADERMAL";

    /// <summary>A route no injection is given by: by mouth, through a tube, on the skin, in the eye, inhaled…</summary>
    public static bool IsNonInjectionRoute(string? route) => route?.Trim().ToUpperInvariant() is "ORAL" or "PO" or "SUBLINGUAL"
        or "NASOGASTRIC" or "RECTAL" or "TOPICAL" or "INHALATION" or "NEBULISATION" or "NEBULIZATION" or "EYE" or "EAR"
        or "NASAL" or "VAGINAL";

    [GeneratedRegex(@"\b(inj(ection)?s?\b|inj\.|infusion|vials?\b|amp(o)?ules?\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Injection();

    [GeneratedRegex(@"(inhal|nebuli[sz]|respule|rotacap|metered[- ]dose|\beye\b|ophthalm|\bear\b|\botic\b|nasal|suppositor|rectal|enema|pessar|vaginal|sublingual|cream|ointment|\bgel\b|lotion|jelly|dusting|\bpaint\b|patch|topical)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Local();

    [GeneratedRegex(@"(tablet|\btabs?\b|capsule|\bcaps?\b|syrup|suspension|\boral\b|sachet|granule|elixir|lozenge|chewable)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Oral();
}

/// <summary>
/// Every line names the route it is given by, and the route must fit its dosage form (ERX-02): an injection is given by
/// needle (IV, IM and SC are the prescriber's choice, as is "Other" for intrathecal or intra-articular), never by a route
/// such as Oral; a tablet or a syrup is not given IV, IM or SC. The route goes to the pharmacy label and the nurse's MAR,
/// so a line without one, or one its form contradicts, blocks signing, whichever screen, favourite or client filled it.
/// </summary>
public static class RouteCheck
{
    public static IEnumerable<SafetyAlert> Run(IReadOnlyList<SafetyDrug> items)
    {
        foreach (var item in items.Where(i => !i.IsCurrentMedication))
        {
            var kind = DosageForms.Classify(item.DosageForm, item.DrugName);
            var problem = kind switch
            {
                DosageFormKind.Injection when string.IsNullOrWhiteSpace(item.Route) =>
                    $"{item.DrugName} is an injection: choose how it is given (IV, IM or SC).",
                _ when string.IsNullOrWhiteSpace(item.Route) => $"{item.DrugName} has no route: choose how it is given.",
                DosageFormKind.Injection when DosageForms.IsNonInjectionRoute(item.Route) =>
                    $"{item.DrugName} is an injection, but its route is {item.Route!.Trim()}.",
                DosageFormKind.Oral when DosageForms.IsInjectionRoute(item.Route) =>
                    $"{item.DrugName} is taken by mouth, but its route is {item.Route!.Trim()}.",
                _ => null,
            };
            if (problem is not null)
            {
                yield return new SafetyAlert($"route:{item.Id}", SafetyAlertKind.Route, SafetyAlertSeverity.Major, IsBlocking: true, problem,
                    "The route is printed on the pharmacy label and the medication chart. Choose the route the medicine is given by.", [item.Id]);
            }
        }
    }
}
