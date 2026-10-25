namespace HyFib.HealthCare360.Modules.Patients.Domain;

/// <summary>
/// Kinds of record the law keeps more confidential than the rest of the chart (gap R-4). A patient's clinical record
/// carrying one opens only to staff holding that category's permission, to those who may open restricted records, or
/// through audited break-the-glass. Demographics, bills, orders and lists are not affected: the desk, the lab and the
/// pharmacy keep serving the patient.
/// </summary>
[Flags]
public enum SensitiveCategory
{
    None = 0,

    /// <summary>HIV infection (HIV and AIDS (Prevention and Control) Act 2017, s.8–11).</summary>
    Hiv = 1,

    /// <summary>Mental illness (Mental Healthcare Act 2017, s.23).</summary>
    MentalHealth = 2,

    /// <summary>Termination of pregnancy (MTP Act s.5A; MTP Regulations 2003).</summary>
    Mtp = 4,
}

public static class SensitiveCategories
{
    public const SensitiveCategory All = SensitiveCategory.Hiv | SensitiveCategory.MentalHealth | SensitiveCategory.Mtp;

    /// <summary>
    /// The category an ICD-10 diagnosis puts a record in, or none: HIV disease (B20–B24) and asymptomatic HIV infection
    /// (Z21); the mental and behavioural disorders from schizophrenia to personality disorders (F20–F69), which are
    /// what the Mental Healthcare Act calls mental illness, but not dementia, substance use or developmental disorders.
    /// A medical abortion (O04) puts it in the MTP category.
    /// </summary>
    public static SensitiveCategory FromIcd(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length < 3)
        {
            return SensitiveCategory.None;
        }

        var c = code.Trim().ToUpperInvariant();
        var block = c[..3];
        return block switch
        {
            "B20" or "B21" or "B22" or "B23" or "B24" or "Z21" => SensitiveCategory.Hiv,
            "O04" => SensitiveCategory.Mtp,
            _ when block[0] == 'F' && int.TryParse(block.AsSpan(1), out var n) && n is >= 20 and <= 69 => SensitiveCategory.MentalHealth,
            _ => SensitiveCategory.None,
        };
    }

    /// <summary>"HIV, mental health" for messages and the audit trail.</summary>
    public static string Describe(SensitiveCategory categories) => string.Join(", ", new[]
    {
        categories.HasFlag(SensitiveCategory.Hiv) ? "HIV" : null,
        categories.HasFlag(SensitiveCategory.MentalHealth) ? "mental health" : null,
        categories.HasFlag(SensitiveCategory.Mtp) ? "termination of pregnancy" : null,
    }.Where(c => c is not null));
}
