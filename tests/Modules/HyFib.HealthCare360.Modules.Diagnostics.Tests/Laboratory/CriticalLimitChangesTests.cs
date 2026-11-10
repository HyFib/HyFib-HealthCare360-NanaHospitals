using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using static HyFib.HealthCare360.Modules.Diagnostics.Tests.TestData;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// Whether new reference intervals let through a value the intervals in force flag critical (LIS-05): only that waits for
/// a second pathologist; a critical limit narrowed or added is made at once.
/// </summary>
public sealed class CriticalLimitChangesTests
{
    private const int Adult = 18 * 365;

    [Fact]
    public void A_critical_limit_moved_away_from_the_interval_or_taken_away_loosens()
    {
        ReferenceRange[] inForce = [Range(3.5m, 5.1m, criticalLow: 2.5m, criticalHigh: 6.5m)];

        Loosened(inForce, [Range(3.5m, 5.1m, criticalLow: 2.5m, criticalHigh: 7.0m)])
            .ShouldContain("Critical high 6.5 → 7.0 (male, age not known)");
        Loosened(inForce, [Range(3.5m, 5.1m, criticalLow: 2.0m, criticalHigh: 6.5m)]).ShouldContain(l => l.StartsWith("Critical low 2.5 → 2.0", StringComparison.Ordinal));
        Loosened(inForce, [Range(3.5m, 5.1m, criticalLow: 2.5m)]).ShouldContain(l => l.StartsWith("Critical high 6.5 → none", StringComparison.Ordinal));
        Loosened(inForce, []).ShouldNotBeEmpty("no interval at all flags nothing critical");
    }

    [Fact]
    public void A_critical_limit_narrowed_added_or_left_as_it_is_does_not_loosen()
    {
        ReferenceRange[] inForce = [Range(3.5m, 5.1m, criticalLow: 2.5m, criticalHigh: 6.5m)];

        Loosened(inForce, [Range(3.5m, 5.1m, criticalLow: 2.8m, criticalHigh: 6.0m)]).ShouldBeEmpty();
        Loosened(inForce, [Range(3.4m, 5.3m, criticalLow: 2.5m, criticalHigh: 6.5m)]).ShouldBeEmpty("the reference interval is not a critical limit");
        Loosened([Range(3.5m, 5.1m)], [Range(3.5m, 5.1m, criticalHigh: 6.5m)]).ShouldBeEmpty();
        Loosened([], [Range(3.5m, 5.1m, criticalHigh: 6.5m)]).ShouldBeEmpty("a new test's first intervals");
        Loosened(inForce, inForce).ShouldBeEmpty();
    }

    [Fact]
    public void Every_patient_is_compared_so_a_band_split_or_a_sex_specific_interval_cannot_hide_a_looser_limit()
    {
        ReferenceRange[] inForce = [Range(12m, 16m, criticalLow: 7m, criticalHigh: 20m)];

        // Split by sex: the women's critical low is lowered, the men's is the same.
        var split = Loosened(inForce, [Range(13m, 17m, criticalLow: 7m, criticalHigh: 20m, gender: Gender.Male), Range(12m, 15m, criticalLow: 6m, criticalHigh: 20m, gender: Gender.Female)]);
        split.ShouldContain("Critical low 7 → 6 (female, age not known)");
        split.ShouldNotContain(l => l.Contains("(male", StringComparison.Ordinal));

        // Split by age: adults keep the limits, children from birth lose the critical high.
        var banded = Loosened(inForce, [Range(12m, 16m, criticalLow: 7m, from: 0, to: Adult), Range(12m, 16m, criticalLow: 7m, criticalHigh: 20m, from: Adult)]);
        banded.ShouldContain("Critical high 20 → none (male, from birth)");
        banded.ShouldNotContain(l => l.Contains("from 18 years", StringComparison.Ordinal));
    }

    [Fact]
    public void A_critical_answer_no_longer_critical_loosens_and_one_added_does_not()
    {
        ReferenceRange[] inForce = [Answers(["Negative"], ["Positive for P. falciparum"])];

        Loosened(inForce, [Answers(["Negative"])]).ShouldContain("No longer critical: Positive for P. falciparum (male, age not known)");
        Loosened(inForce, [Answers(["Negative"], ["Positive for P. falciparum", "Positive for P. vivax"])]).ShouldBeEmpty();
        Loosened(inForce, [Answers(["Negative"], ["positive for p. falciparum"])]).ShouldBeEmpty("answers are compared as the lab reads them");
    }

    private static IReadOnlyList<string> Loosened(IReadOnlyCollection<ReferenceRange> inForce, IReadOnlyCollection<ReferenceRange> asked) =>
        CriticalLimitChanges.Loosened(inForce, asked, decimals: 1);
}
