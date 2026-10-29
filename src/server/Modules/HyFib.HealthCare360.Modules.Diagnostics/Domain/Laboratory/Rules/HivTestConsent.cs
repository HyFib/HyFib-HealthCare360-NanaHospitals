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
    /// Whether the test, or a test under it, is an HIV test: by its LOINC code, or by "HIV" as a word of its code or name
    /// ("LAB-HIV", "Anti-HIV 1 & 2", "HIV-1 RNA"). Under a panel are its analytes and the panels within it, followed as
    /// the laboratory expands them, to <see cref="LabTest.MaxPanelDepth"/>; <paramref name="tests"/> holds them by id.
    /// </summary>
    public static bool Needed(LabTest test, IReadOnlyDictionary<Guid, LabTest> tests) => Needed(test, tests, 0);

    private static bool Needed(LabTest test, IReadOnlyDictionary<Guid, LabTest> tests, int depth) =>
        IsHiv(test)
        || (depth < LabTest.MaxPanelDepth
            && test.Components.Any(c => tests.TryGetValue(c.TestId, out var component) && Needed(component, tests, depth + 1)));

    /// <summary>
    /// Whether a result is an HIV test's: its own test is one, or a panel it is reported under is, on the way down from
    /// the test ordered (the bands of an "HIV confirmation" panel). A released result outside its normal answers puts the
    /// patient's record in the HIV category (<c>LabReportService</c>). Another test of a panel that merely holds an HIV
    /// test, such as the hepatitis B test beside it in viral markers, is not one.
    /// </summary>
    /// <param name="ordered">The test ordered, with <paramref name="tests"/> holding the tests under it by id.</param>
    /// <param name="resultTestId">The test the result is for: the one ordered, or an analyte under it.</param>
    public static bool IsHivResult(LabTest? ordered, Guid resultTestId, IReadOnlyDictionary<Guid, LabTest> tests) =>
        (tests.TryGetValue(resultTestId, out var own) && IsHiv(own))
        || (ordered is not null && ReportedUnderHiv(ordered, resultTestId, tests, hivAbove: false, depth: 0));

    private static bool ReportedUnderHiv(LabTest test, Guid resultTestId, IReadOnlyDictionary<Guid, LabTest> tests, bool hivAbove, int depth)
    {
        var hiv = hivAbove || IsHiv(test);
        return test.Id == resultTestId
            ? hiv
            : depth < LabTest.MaxPanelDepth
              && test.Components.Any(c => tests.TryGetValue(c.TestId, out var component) && ReportedUnderHiv(component, resultTestId, tests, hiv, depth + 1));
    }

    /// <summary>Whether the test is an HIV test.</summary>
    public static bool IsHiv(LabTest test) =>
        (test.Loinc is { } loinc && Loinc.Contains(loinc.Trim())) || HivWord().IsMatch(test.Code) || HivWord().IsMatch(test.Name);

    /// <summary>"HIV" not inside a longer word: digits may follow ("HIV1"), letters may not.</summary>
    [GeneratedRegex("(?<![A-Za-z])HIV(?![A-Za-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HivWord();
}
