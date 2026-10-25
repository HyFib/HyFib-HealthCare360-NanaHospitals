namespace HyFib.HealthCare360.Modules.Clinical.Domain.Stewardship;

/// <summary>The WHO AWaRe groups of antibiotics: first choices, broader ones to watch, and the last resort to reserve.</summary>
public enum AwareGroup
{
    Access = 0,
    Watch,
    Reserve,
}

/// <summary>An antibiotic the hospital knows: its AWaRe group, and whether the stewardship policy restricts it by default.</summary>
public sealed record Antimicrobial(string Name, AwareGroup Group, bool RestrictedByDefault, params string[] Parts);

/// <summary>
/// The WHO AWaRe classification (2023) of the antibiotics used in Indian hospitals, matched on the generic name. The
/// hospital's restricted-antibiotic policy (ICMR AMSP guidance) needs stewardship approval for every Reserve antibiotic
/// and, by default, for the broad Watch ones a hospital usually restricts: carbapenems, piperacillin–tazobactam,
/// cefoperazone–sulbactam and the glycopeptides. A combination matches only when every part is in the name.
/// </summary>
public static class Aware
{
    public static IReadOnlyList<Antimicrobial> All { get; } =
    [
        // Access
        new("Amoxicillin", AwareGroup.Access, false, "amoxicillin"),
        new("Amoxicillin–clavulanate", AwareGroup.Access, false, "amoxicillin", "clavulan"),
        new("Ampicillin", AwareGroup.Access, false, "ampicillin"),
        new("Benzylpenicillin", AwareGroup.Access, false, "benzylpenicillin"),
        new("Cloxacillin", AwareGroup.Access, false, "cloxacillin"),
        new("Cefazolin", AwareGroup.Access, false, "cefazolin"),
        new("Cefalexin", AwareGroup.Access, false, "cefalexin"),
        new("Cephalexin", AwareGroup.Access, false, "cephalexin"),
        new("Doxycycline", AwareGroup.Access, false, "doxycycline"),
        new("Metronidazole", AwareGroup.Access, false, "metronidazole"),
        new("Nitrofurantoin", AwareGroup.Access, false, "nitrofurantoin"),
        new("Gentamicin", AwareGroup.Access, false, "gentamicin"),
        new("Amikacin", AwareGroup.Access, false, "amikacin"),
        new("Co-trimoxazole", AwareGroup.Access, false, "sulfamethoxazole"),
        new("Clindamycin", AwareGroup.Access, false, "clindamycin"),
        new("Chloramphenicol", AwareGroup.Access, false, "chloramphenicol"),

        // Watch
        new("Azithromycin", AwareGroup.Watch, false, "azithromycin"),
        new("Clarithromycin", AwareGroup.Watch, false, "clarithromycin"),
        new("Erythromycin", AwareGroup.Watch, false, "erythromycin"),
        new("Ciprofloxacin", AwareGroup.Watch, false, "ciprofloxacin"),
        new("Levofloxacin", AwareGroup.Watch, false, "levofloxacin"),
        new("Moxifloxacin", AwareGroup.Watch, false, "moxifloxacin"),
        new("Ofloxacin", AwareGroup.Watch, false, "ofloxacin"),
        new("Cefuroxime", AwareGroup.Watch, false, "cefuroxime"),
        new("Ceftriaxone", AwareGroup.Watch, false, "ceftriaxone"),
        new("Cefotaxime", AwareGroup.Watch, false, "cefotaxime"),
        new("Ceftazidime", AwareGroup.Watch, false, "ceftazidime"),
        new("Cefixime", AwareGroup.Watch, false, "cefixime"),
        new("Cefpodoxime", AwareGroup.Watch, false, "cefpodoxime"),
        new("Cefepime", AwareGroup.Watch, false, "cefepime"),
        new("Cefoperazone–sulbactam", AwareGroup.Watch, true, "cefoperazone", "sulbactam"),
        new("Piperacillin–tazobactam", AwareGroup.Watch, true, "piperacillin", "tazobactam"),
        new("Meropenem", AwareGroup.Watch, true, "meropenem"),
        new("Imipenem–cilastatin", AwareGroup.Watch, true, "imipenem"),
        new("Ertapenem", AwareGroup.Watch, true, "ertapenem"),
        new("Vancomycin", AwareGroup.Watch, true, "vancomycin"),
        new("Teicoplanin", AwareGroup.Watch, true, "teicoplanin"),
        new("Tobramycin", AwareGroup.Watch, false, "tobramycin"),
        new("Rifampicin", AwareGroup.Watch, false, "rifampicin"),

        // Fosfomycin is Reserve only intravenously; the name cannot tell, so it is listed as the oral form a hospital mostly uses.
        new("Fosfomycin", AwareGroup.Watch, false, "fosfomycin"),

        // Reserve: always restricted.
        new("Ceftazidime–avibactam", AwareGroup.Reserve, true, "ceftazidime", "avibactam"),
        new("Ceftolozane–tazobactam", AwareGroup.Reserve, true, "ceftolozane", "tazobactam"),
        new("Meropenem–vaborbactam", AwareGroup.Reserve, true, "meropenem", "vaborbactam"),
        new("Aztreonam", AwareGroup.Reserve, true, "aztreonam"),
        new("Cefiderocol", AwareGroup.Reserve, true, "cefiderocol"),
        new("Colistin", AwareGroup.Reserve, true, "colistin"),
        new("Polymyxin B", AwareGroup.Reserve, true, "polymyxin"),
        new("Tigecycline", AwareGroup.Reserve, true, "tigecycline"),
        new("Linezolid", AwareGroup.Reserve, true, "linezolid"),
        new("Tedizolid", AwareGroup.Reserve, true, "tedizolid"),
        new("Daptomycin", AwareGroup.Reserve, true, "daptomycin"),
        new("Faropenem", AwareGroup.Reserve, true, "faropenem"),
        new("Dalbavancin", AwareGroup.Reserve, true, "dalbavancin"),
        new("Plazomicin", AwareGroup.Reserve, true, "plazomicin"),
    ];

    /// <summary>
    /// The antibiotic a generic (or brand) name is, the most specific match first: "Ceftazidime + Avibactam" is the Reserve
    /// combination, not plain ceftazidime, and levofloxacin is not ofloxacin. Null when the name is no antibiotic the
    /// hospital knows.
    /// </summary>
    public static Antimicrobial? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = name.ToLowerInvariant();
        return All.Where(a => a.Parts.All(p => text.Contains(p, StringComparison.Ordinal)))
            .OrderByDescending(a => a.Parts.Length)
            .ThenByDescending(a => a.Parts.Sum(p => p.Length))
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether the stewardship policy restricts the antibiotic: by default (every Reserve antibiotic and the broad Watch
    /// ones above), or because the hospital added its name in <c>ams.restricted.extra</c>.
    /// </summary>
    public static bool IsRestricted(Antimicrobial antimicrobial, IReadOnlyCollection<string> extra) =>
        antimicrobial.RestrictedByDefault
        || extra.Any(e => antimicrobial.Name.Contains(e, StringComparison.OrdinalIgnoreCase) || antimicrobial.Parts.Contains(e.ToLowerInvariant()));

    /// <summary>How the culture's isolate responded to the antibiotic, from the antibiogram's free-text antibiotic names; null when not tested.</summary>
    public static string? Susceptibility(Antimicrobial antimicrobial, IEnumerable<(string Antibiotic, string Interpretation)> antibiogram) =>
        antibiogram.Where(s => Find(s.Antibiotic) is { } tested && tested.Name == antimicrobial.Name)
            .Select(s => s.Interpretation)
            .FirstOrDefault();
}
