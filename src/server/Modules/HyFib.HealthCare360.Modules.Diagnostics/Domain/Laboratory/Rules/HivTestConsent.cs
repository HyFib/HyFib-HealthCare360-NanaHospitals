using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// Informed consent before an HIV test. The HIV and AIDS (Prevention and Control) Act 2017, s.5, lets no HIV test be
/// undertaken without the informed consent of the person tested or their representative, given after pre-test
/// counselling. Of the exceptions s.6 allows, the one a hospital laboratory meets is a court's order. The sample of an
/// HIV test is therefore not drawn until that consent, or the order, is recorded on the test.
/// </summary>
public static partial class HivTestConsent
{
    /// <summary>
    /// LOINC codes of HIV tests: antibody screens (HIV-1, HIV-1/2, HIV-1/2 with the p24 antigen), the HIV-1 immunoblot,
    /// and the HIV-1 viral load.
    /// </summary>
    private static readonly HashSet<string> Loinc = new(["56888-1", "7917-8", "31201-7", "5221-7", "20447-9"], StringComparer.Ordinal);

    /// <summary>
    /// Whether the test, or one of its <paramref name="components"/> (the analytes of a panel), is an HIV test: by its
    /// LOINC code, or by "HIV" as a word of its code or name ("LAB-HIV", "Anti-HIV 1 & 2", "HIV-1 RNA").
    /// </summary>
    public static bool Needed(LabTest test, IEnumerable<LabTest> components) => IsHiv(test) || components.Any(IsHiv);

    public static bool IsHiv(LabTest test) =>
        (test.Loinc is { } loinc && Loinc.Contains(loinc.Trim())) || HivWord().IsMatch(test.Code) || HivWord().IsMatch(test.Name);

    /// <summary>"HIV" not inside a longer word: digits may follow ("HIV1"), letters may not.</summary>
    [GeneratedRegex("(?<![A-Za-z])HIV(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HivWord();
}
