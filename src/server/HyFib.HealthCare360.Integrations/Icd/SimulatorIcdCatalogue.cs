namespace HyFib.HealthCare360.Integrations.Icd;

/// <summary>
/// The ICD lookup simulator (provider <c>simulator</c>): a short built-in list of common codes, answered the way the WHO
/// ICD-API answers (ICD-11 searched by words or code, ICD-10 looked up by code with its subcategories), so coders can
/// practise online lookup without internet access or WHO credentials. The codes and titles are real, but the list is a
/// small sample, not the classification.
/// </summary>
internal static class SimulatorIcdCatalogue
{
    private sealed record Entry(string Code, string Title, string? Chapter, bool IsLeaf = true);

    private static readonly Entry[] Icd11 =
    [
        new("1A00", "Cholera", "01"),
        new("1A07", "Typhoid fever", "01"),
        new("1D20", "Dengue without warning signs", "01"),
        new("1D21", "Dengue with warning signs", "01"),
        new("1D22", "Severe dengue", "01"),
        new("1F40", "Malaria due to Plasmodium falciparum", "01"),
        new("1F41", "Malaria due to Plasmodium vivax", "01"),
        new("2C25", "Malignant neoplasms of bronchus or lung", "02", IsLeaf: false),
        new("3A00", "Iron deficiency anaemia", "03"),
        new("5A10", "Type 1 diabetes mellitus", "05"),
        new("5A11", "Type 2 diabetes mellitus", "05"),
        new("5B81", "Obesity", "05"),
        new("6A70", "Single episode depressive disorder", "06", IsLeaf: false),
        new("8A80", "Migraine", "08", IsLeaf: false),
        new("8B11", "Cerebral ischaemic stroke", "08"),
        new("9B10", "Age-related cataract", "09"),
        new("BA00", "Essential hypertension", "11"),
        new("BA41", "Acute myocardial infarction", "11"),
        new("BD10", "Congestive heart failure", "11"),
        new("CA00", "Acute nasopharyngitis", "12"),
        new("CA02", "Acute pharyngitis", "12"),
        new("CA22", "Chronic obstructive pulmonary disease", "12", IsLeaf: false),
        new("CA23", "Asthma", "12", IsLeaf: false),
        new("CA40", "Pneumonia", "12", IsLeaf: false),
        new("GB61", "Chronic kidney disease", "16", IsLeaf: false),
        new("GC08", "Urinary tract infection, site not specified", "16"),
        new("RA01", "COVID-19", "25", IsLeaf: false),
    ];

    private static readonly Entry[] Icd10 =
    [
        new("A00", "Cholera", null, IsLeaf: false),
        new("A00.0", "Cholera due to Vibrio cholerae 01, biovar cholerae", null),
        new("A00.1", "Cholera due to Vibrio cholerae 01, biovar eltor", null),
        new("A00.9", "Cholera, unspecified", null),
        new("A01.0", "Typhoid fever", null),
        new("A90", "Dengue fever [classical dengue]", null),
        new("A91", "Dengue haemorrhagic fever", null),
        new("B50", "Plasmodium falciparum malaria", null, IsLeaf: false),
        new("B50.9", "Plasmodium falciparum malaria, unspecified", null),
        new("B54", "Unspecified malaria", null),
        new("D50", "Iron deficiency anaemia", null, IsLeaf: false),
        new("D50.9", "Iron deficiency anaemia, unspecified", null),
        new("E11", "Type 2 diabetes mellitus", null, IsLeaf: false),
        new("I10", "Essential (primary) hypertension", null),
        new("I21", "Acute myocardial infarction", null, IsLeaf: false),
        new("I21.9", "Acute myocardial infarction, unspecified", null),
        new("I50", "Heart failure", null, IsLeaf: false),
        new("I50.0", "Congestive heart failure", null),
        new("J00", "Acute nasopharyngitis [common cold]", null),
        new("J18", "Pneumonia, organism unspecified", null, IsLeaf: false),
        new("J18.9", "Pneumonia, unspecified", null),
        new("J45", "Asthma", null, IsLeaf: false),
        new("J45.9", "Asthma, unspecified", null),
        new("N18", "Chronic kidney disease", null, IsLeaf: false),
        new("N18.9", "Chronic kidney disease, unspecified", null),
        new("N39.0", "Urinary tract infection, site not specified", null),
        new("U07.1", "COVID-19, virus identified", null),
    ];

    /// <summary>ICD-11 entries whose title holds every word of the query, or whose code starts with it.</summary>
    public static IReadOnlyList<IcdSearchResult> SearchIcd11(string query, int limit, string release)
    {
        var text = query.Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Icd11
            .Where(e => e.Code.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                        || words.All(w => e.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .Select(e => new IcdSearchResult(e.Code, e.Title, $"ICD-11 MMS {release}", e.Chapter, e.IsLeaf))
            .ToList();
    }

    /// <summary>The ICD-10 category with this code and its subcategories, as WHO's ICD-10 lookup answers; none when unknown.</summary>
    public static IReadOnlyList<IcdSearchResult> LookupIcd10(string code, int limit, string release)
    {
        var wanted = code.Trim().ToUpperInvariant();
        if (!Icd10.Any(e => e.Code == wanted))
        {
            return [];
        }

        return Icd10
            .Where(e => e.Code == wanted || e.Code.StartsWith(wanted + ".", StringComparison.Ordinal))
            .Take(limit)
            .Select(e => new IcdSearchResult(e.Code, e.Title, $"ICD-10 {release}", null, e.IsLeaf))
            .ToList();
    }
}
