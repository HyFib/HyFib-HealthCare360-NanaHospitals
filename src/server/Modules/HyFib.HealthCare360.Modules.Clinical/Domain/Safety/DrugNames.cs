using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

/// <summary>
/// Normalises drug names for safety matching. Indian labels mix British and US spellings ("frusemide",
/// "amoxycillin", "sulphamethoxazole"), salt names ("losartan potassium") and combinations ("Ibuprofen +
/// Paracetamol"), so every check compares canonical, salt-free ingredient phrases as whole words.
/// </summary>
public static partial class DrugNames
{
    // Ordered longest-first so multi-word phrases win over their parts.
    private static readonly (string From, string To)[] Synonyms =
    [
        ("potassium clavulanate", "clavulanate"),
        ("clavulanic acid", "clavulanate"),
        ("acetylsalicylic acid", "aspirin"),
        ("glyceryl trinitrate", "nitroglycerin"),
        ("ethinyl oestradiol", "ethinylestradiol"),
        ("ethinyl estradiol", "ethinylestradiol"),
        ("divalproex sodium", "valproate"),
        ("sodium valproate", "valproate"),
        ("valproic acid", "valproate"),
        ("co trimoxazole", "sulfamethoxazole + trimethoprim"),
        ("cotrimoxazole", "sulfamethoxazole + trimethoprim"),
        ("ethinyloestradiol", "ethinylestradiol"),
        ("acetaminophen", "paracetamol"),
        ("amoxycillin", "amoxicillin"),
        ("frusemide", "furosemide"),
        ("adrenaline", "epinephrine"),
        ("noradrenaline", "norepinephrine"),
        ("lignocaine", "lidocaine"),
        ("albuterol", "salbutamol"),
        ("thyroxine", "levothyroxine"),
        ("divalproex", "valproate"),
        ("cephalexin", "cefalexin"),
        ("cephazolin", "cefazolin"),
        ("sulphamethoxazole", "sulfamethoxazole"),
        ("sulphasalazine", "sulfasalazine"),
        ("sulphadiazine", "sulfadiazine"),
        ("oestradiol", "estradiol"),
        ("acyclovir", "aciclovir"),
        ("rifampin", "rifampicin"),
        ("gtn", "nitroglycerin"),
    ];

    /// <summary>Salt/hydrate words dropped from the end of a name when a real ingredient word precedes them.</summary>
    private static readonly HashSet<string> SaltWords = new(StringComparer.Ordinal)
    {
        "hydrochloride", "hcl", "dihydrochloride", "hydrobromide", "sodium", "disodium", "potassium", "calcium", "magnesium",
        "besylate", "besilate", "maleate", "mesylate", "mesilate", "succinate", "tartrate", "citrate", "sulfate", "sulphate",
        "phosphate", "fumarate", "hyclate", "monohydrate", "dihydrate", "trihydrate", "anhydrous", "acetate", "bromide",
        "dipropionate", "propionate", "furoate", "valerate", "xinafoate", "tromethamine", "trometamol",
    };

    [GeneratedRegex(@"[^a-z0-9+]+")]
    private static partial Regex NonWord();

    /// <summary>Combination separators written on labels ("A+B", "A/B", "A, B", "A &amp; B").</summary>
    [GeneratedRegex(@"[+/,&;]")]
    private static partial Regex CombinationMark();

    [GeneratedRegex(@"\s*(\+|\band\b|\bwith\b)\s*")]
    private static partial Regex ComponentSeparator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Lower-case, punctuation-free text with canonical spellings (combination "+" markers kept).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var marked = CombinationMark().Replace(text.ToLowerInvariant(), " + ");
        var value = " " + Spaces().Replace(NonWord().Replace(marked, " "), " ").Trim() + " ";
        foreach (var (from, to) in Synonyms)
        {
            value = value.Replace($" {from} ", $" {to} ", StringComparison.Ordinal);
        }

        return value.Trim();
    }

    /// <summary>
    /// Canonical active ingredients of a medicine: the generic composition split into components, salts removed.
    /// Falls back to the product name when the generic is unknown (free-text prescriptions).
    /// </summary>
    public static IReadOnlyList<string> Components(string? genericName, string? drugName = null)
    {
        var source = string.IsNullOrWhiteSpace(genericName) ? drugName : genericName;
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        var normalized = Normalize(source);
        return ComponentSeparator().Split(normalized)
            .Select(part => StripSalts(part.Trim()))
            .Where(part => part.Length > 1 && part is not "and" and not "with")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when <paramref name="term"/> occurs in <paramref name="text"/> as whole words ("warfarin" in "warfarin 5 mg").</summary>
    public static bool ContainsTerm(string text, string term)
    {
        if (text.Length == 0 || term.Length == 0)
        {
            return false;
        }

        return $" {text} ".Contains($" {term} ", StringComparison.Ordinal);
    }

    /// <summary>True when either phrase contains the other as whole words (matches "amoxicillin" with "amoxicillin clavulanate").</summary>
    public static bool Overlaps(string a, string b) => ContainsTerm(a, b) || ContainsTerm(b, a);

    private static string StripSalts(string component)
    {
        var words = component.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && SaltWords.Contains(words[^1]) && words.Take(words.Count - 1).Any(w => !SaltWords.Contains(w)))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }
}
