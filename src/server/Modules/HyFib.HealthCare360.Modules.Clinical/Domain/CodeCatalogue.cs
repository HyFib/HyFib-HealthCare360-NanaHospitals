using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public static partial class CodeReleases
{
    public const string Icd10 = "ICD-10 2019";
    public const string Icd11 = "ICD-11 MMS";
    public const string Icd10Pcs = "ICD-10-PCS";

    /// <summary>
    /// The catalogue's own label for a release label a caller or the WHO ICD-API used: <c>ICD-11 MMS 2025-01</c>,
    /// <c>ICD-11</c> or <c>11</c> are ICD-11 MMS; <c>ICD-10</c>, <c>icd10</c> or <c>10</c> are ICD-10 2019; <c>ICD-10-PCS</c>
    /// is ICD-10-PCS. Null for anything else, including an empty label.
    /// </summary>
    public static string? Normalize(string? release)
    {
        var compact = new string((release ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return compact switch
        {
            "" => null,
            _ when compact.StartsWith("icd10pcs", StringComparison.Ordinal) => Icd10Pcs,
            "11" => Icd11,
            _ when compact.StartsWith("icd11", StringComparison.Ordinal) => Icd11,
            "10" => Icd10,
            _ when compact.StartsWith("icd10", StringComparison.Ordinal) && !compact.StartsWith("icd10cm", StringComparison.Ordinal) => Icd10,
            _ => null,
        };
    }

    /// <summary>
    /// The release a diagnosis code belongs to, from its form. WHO made the two forms distinct on purpose: an ICD-10 code
    /// has a digit in second place (<c>E11.9</c>), an ICD-11 MMS code always has a letter there (<c>5A11</c>, <c>1A00</c>).
    /// A post-coordinated ICD-11 cluster (<c>1A00&amp;XN4F</c>) is judged by its first stem code. Null when the text is
    /// neither.
    /// </summary>
    public static string? OfDiagnosisCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var stem = code.Trim().ToUpperInvariant().Split('&', '/')[0];
        return Icd10Code().IsMatch(stem) ? Icd10 : Icd11Code().IsMatch(stem) ? Icd11 : null;
    }

    public static bool IsIcd10Code(string? code) => code is not null && Icd10Code().IsMatch(code.Trim().ToUpperInvariant());

    /// <summary>A letter and two digits, then optionally a dot and one to three characters: A00, E11.9, M54.16.</summary>
    [GeneratedRegex(@"^[A-Z][0-9]{2}(\.[0-9A-Z]{1,3})?$")]
    private static partial Regex Icd10Code();

    /// <summary>ICD-11 MMS stem codes (no I or O): 1A00, 5A11, BA00.Z, XN4F. The second character is always a letter.</summary>
    [GeneratedRegex(@"^[0-9A-HJ-NP-Z][A-HJ-NP-Z][0-9][0-9A-HJ-NP-Z](\.[0-9A-HJ-NP-Z]{1,4})?$")]
    private static partial Regex Icd11Code();
}

public enum CodeSource
{
    Seed = 0,

    /// <summary>Cached from the WHO ICD-API through the ICD lookup integration.</summary>
    Online,
    Manual,

    /// <summary>
    /// From the hospital's own copy of the WHO ICD-10 ClaML file or the CMS ICD-10-PCS order file (Medical records ›
    /// ICD catalogue › Import, or the server's <c>catalogue import</c> command).
    /// </summary>
    Import,
}

/// <summary>
/// The ICD-10 dagger and asterisk convention: a dagger code names the underlying disease, an asterisk code the way it
/// shows in an organ (A17.0† Tuberculous meningitis, G01* Meningitis in bacterial diseases classified elsewhere).
/// </summary>
public enum IcdUsage
{
    Dagger = 0,
    Asterisk,
}

/// <summary>
/// Local diagnosis catalogue (MRD-01, INT-ICD). Seeded with common WHO ICD-10 (2019) codes and completed by importing
/// the hospital's own WHO ClaML file; ICD-11 MMS codes found online can be cached here. <see cref="IsBillable"/> marks
/// codes valid for coding and claims.
/// <para>
/// Not audited row by row: an import writes tens of thousands of rows, and its import record and one audit entry (file,
/// SHA-256, counts) say what changed. Changes made by hand through the API are audited explicitly.
/// </para>
/// </summary>
[NotAudited]
public sealed class IcdCode : AuditableEntity
{
    public const int MaxIncludedTermsLength = 2000;

    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Release { get; set; } = CodeReleases.Icd10;

    /// <summary>Chapter in Roman numerals (ICD-10) or the ICD-11 chapter number.</summary>
    public string? Chapter { get; set; }

    /// <summary>Block range, for example "E10-E14".</summary>
    public string? Block { get; set; }

    public bool IsBillable { get; set; } = true;

    /// <summary>Dagger (†) or asterisk (*) code; null for most codes.</summary>
    public IcdUsage? Usage { get; set; }

    /// <summary>Common clinical shorthand and synonyms (T2DM, HTN, AGE, URTI…) that make the search forgiving. Never changed by an import.</summary>
    public string? Keywords { get; set; }

    /// <summary>The WHO file's inclusion terms ("Includes: classical cholera"), joined with "; ", for search.</summary>
    public string? IncludedTerms { get; set; }

    /// <summary>Lower-case code + title + keywords + inclusion terms, served by a trigram index.</summary>
    public string SearchText { get; private set; } = string.Empty;

    public CodeSource Source { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When an import found the code no longer in the WHO file. The code is then inactive (no new diagnoses), and the
    /// diagnoses already recorded keep it. A later import that finds it again makes it active once more.
    /// </summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    public void RefreshSearchText() => SearchText = CodeSearch.BuildSearchText(Code, Title, Keywords, IncludedTerms);
}

/// <summary>Local procedure catalogue (ICD-10-PCS / ICHI style) used for coding at discharge. Audited like <see cref="IcdCode"/>.</summary>
[NotAudited]
public sealed class ProcedureCode : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string System { get; set; } = CodeReleases.Icd10Pcs;

    /// <summary>Speciality grouping for browsing (General surgery, Obstetrics, Cardiology…), or the PCS table for imported codes.</summary>
    public string? Category { get; set; }

    public string? Keywords { get; set; }
    public string SearchText { get; private set; } = string.Empty;
    public CodeSource Source { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>When an import found the code no longer in the CMS order file (see <see cref="IcdCode.WithdrawnAt"/>).</summary>
    public DateTimeOffset? WithdrawnAt { get; set; }

    public void RefreshSearchText() => SearchText = CodeSearch.BuildSearchText(Code, Title, Keywords, null);
}

public static class CodeSearch
{
    public static string BuildSearchText(string code, string title, string? keywords, string? includedTerms) =>
        string.Join(' ', new[] { code, title, keywords, includedTerms }.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
}
