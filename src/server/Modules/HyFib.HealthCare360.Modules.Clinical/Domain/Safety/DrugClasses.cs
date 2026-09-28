namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>A drug class used for class-level allergy and duplicate checks.</summary>
public sealed record DrugClass(string Key, string Name, IReadOnlyList<string> Aliases, IReadOnlyList<string> Members);

/// <summary>Known cross-sensitivity between two classes (for example penicillins → cephalosporins).</summary>
public sealed record CrossSensitivity(string FromClass, string ToClass, SafetyAlertSeverity Severity, string Note);

/// <summary>
/// Class memberships that matter for allergy safety: a patient labelled "penicillin allergic" must be warned about
/// amoxicillin, and an "NSAID" or aspirin allergy covers ibuprofen or diclofenac. Names are canonical (see
/// <see cref="DrugNames"/>).
/// </summary>
public static class DrugClasses
{
    public const string Penicillins = "penicillins";
    public const string Cephalosporins = "cephalosporins";
    public const string Carbapenems = "carbapenems";
    public const string Nsaids = "nsaids";

    public static IReadOnlyList<DrugClass> All { get; } =
    [
        new(Penicillins, "Penicillins", ["penicillin", "penicillins", "pcn"],
            ["penicillin", "benzylpenicillin", "phenoxymethylpenicillin", "amoxicillin", "ampicillin", "cloxacillin", "dicloxacillin",
             "flucloxacillin", "piperacillin", "ticarcillin"]),
        new(Cephalosporins, "Cephalosporins", ["cephalosporin", "cephalosporins"],
            ["cefalexin", "cefadroxil", "cefazolin", "cefuroxime", "cefaclor", "cefprozil", "cefixime", "cefpodoxime", "cefdinir",
             "ceftriaxone", "cefotaxime", "ceftazidime", "cefoperazone", "cefepime", "ceftaroline", "cefoxitin", "ceftizoxime"]),
        new(Carbapenems, "Carbapenems", ["carbapenem", "carbapenems"], ["meropenem", "imipenem", "ertapenem", "doripenem", "faropenem"]),
        new("sulfonamides", "Sulfonamide antibiotics",
            ["sulfa", "sulpha", "sulfa drugs", "sulpha drugs", "sulfonamide", "sulfonamides", "sulphonamide", "sulphonamides"],
            ["sulfamethoxazole", "sulfadiazine", "sulfasalazine", "sulfacetamide"]),
        new(Nsaids, "NSAIDs", ["nsaid", "nsaids", "non steroidal anti inflammatory", "non steroidal anti inflammatory drugs"],
            ["aspirin", "ibuprofen", "diclofenac", "naproxen", "aceclofenac", "mefenamic acid", "ketorolac", "indomethacin", "piroxicam",
             "meloxicam", "etoricoxib", "celecoxib", "nimesulide", "etodolac", "flurbiprofen", "ketoprofen", "dexketoprofen", "lornoxicam"]),
        new("fluoroquinolones", "Fluoroquinolones", ["quinolone", "quinolones", "fluoroquinolone", "fluoroquinolones"],
            ["ciprofloxacin", "levofloxacin", "ofloxacin", "norfloxacin", "moxifloxacin", "gatifloxacin", "prulifloxacin"]),
        new("macrolides", "Macrolides", ["macrolide", "macrolides"], ["azithromycin", "clarithromycin", "erythromycin", "roxithromycin"]),
        new("tetracyclines", "Tetracyclines", ["tetracyclines"], ["doxycycline", "minocycline", "tetracycline"]),
        new("aminoglycosides", "Aminoglycosides", ["aminoglycoside", "aminoglycosides"], ["gentamicin", "amikacin", "tobramycin", "streptomycin", "netilmicin"]),
        new("opioids", "Opioids", ["opioid", "opioids", "opiate", "opiates"],
            ["morphine", "codeine", "tramadol", "tapentadol", "fentanyl", "pethidine", "oxycodone", "hydromorphone", "buprenorphine", "methadone"]),
        new("ace-inhibitors", "ACE inhibitors", ["ace inhibitor", "ace inhibitors", "acei"],
            ["enalapril", "ramipril", "lisinopril", "perindopril", "captopril", "fosinopril", "trandolapril"]),
    ];

    public static IReadOnlyList<CrossSensitivity> CrossSensitivities { get; } =
    [
        new(Penicillins, Cephalosporins, SafetyAlertSeverity.Moderate, "About 1–2% of penicillin-allergic patients react to cephalosporins (higher with first-generation agents)."),
        new(Penicillins, Carbapenems, SafetyAlertSeverity.Minor, "Cross-reactivity between penicillins and carbapenems is below 1%."),
        new(Cephalosporins, Penicillins, SafetyAlertSeverity.Moderate, "Patients allergic to a cephalosporin may react to penicillins."),
    ];

    /// <summary>Classes whose members appear in the ingredient phrase.</summary>
    public static IReadOnlyList<DrugClass> ClassesOf(string ingredient) =>
        All.Where(c => c.Members.Any(m => DrugNames.ContainsTerm(ingredient, m))).ToList();

    /// <summary>Classes named by an allergen label ("Sulfa drugs", "NSAIDs") or containing it as a member.</summary>
    public static IReadOnlyList<DrugClass> ClassesForAllergen(string normalizedAllergen) =>
        All.Where(c => c.Aliases.Any(a => DrugNames.ContainsTerm(normalizedAllergen, a)) || c.Members.Any(m => DrugNames.ContainsTerm(normalizedAllergen, m)))
            .ToList();
}
