namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public static class CodeReleases
{
    public const string Icd10 = "ICD-10 2019";
    public const string Icd11 = "ICD-11 MMS";
    public const string Icd10Pcs = "ICD-10-PCS";
}

public enum CodeSource
{
    Seed = 0,

    /// <summary>Cached from the WHO ICD-API through the ICD lookup integration.</summary>
    Online,
    Manual,
}

/// <summary>
/// Local diagnosis catalogue (MRD-01, INT-ICD). Seeded with common WHO ICD-10 (2019) codes; ICD-11 MMS codes found
/// online can be cached here. <see cref="IsBillable"/> marks leaf codes (valid for coding and claims).
/// </summary>
public sealed class IcdCode : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Release { get; set; } = CodeReleases.Icd10;

    /// <summary>Chapter in Roman numerals (ICD-10) or the ICD-11 chapter number.</summary>
    public string? Chapter { get; set; }

    /// <summary>Block range, for example "E10-E14".</summary>
    public string? Block { get; set; }

    public bool IsBillable { get; set; } = true;

    /// <summary>Common clinical shorthand and synonyms (T2DM, HTN, AGE, URTI…) that make the search forgiving.</summary>
    public string? Keywords { get; set; }

    /// <summary>Lower-case code + title + keywords, served by a trigram index.</summary>
    public string SearchText { get; private set; } = string.Empty;

    public CodeSource Source { get; set; }
    public bool IsActive { get; set; } = true;

    public void RefreshSearchText() => SearchText = CodeSearch.BuildSearchText(Code, Title, Keywords);
}

/// <summary>Local procedure catalogue (ICD-10-PCS / ICHI style) used for coding at discharge.</summary>
public sealed class ProcedureCode : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string System { get; set; } = CodeReleases.Icd10Pcs;

    /// <summary>Speciality grouping for browsing (General surgery, Obstetrics, Cardiology…).</summary>
    public string? Category { get; set; }

    public string? Keywords { get; set; }
    public string SearchText { get; private set; } = string.Empty;
    public CodeSource Source { get; set; }
    public bool IsActive { get; set; } = true;

    public void RefreshSearchText() => SearchText = CodeSearch.BuildSearchText(Code, Title, Keywords);
}

public static class CodeSearch
{
    public static string BuildSearchText(string code, string title, string? keywords) =>
        string.Join(' ', new[] { code, title, keywords }.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
}
