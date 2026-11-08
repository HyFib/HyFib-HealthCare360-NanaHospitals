using System.Globalization;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

/// <summary>
/// Whether a new set of reference intervals lets a value through that the set in force flags critical (pure; unit-tested).
/// A critical flag is what makes the lab call the doctor before the result is verified (LIS-05), so a looser limit is a
/// call nobody makes. The sets are compared for every patient: each sex (and neither), the age not known, and every age at
/// which an interval of either set starts or ends (between those ages the interval that applies does not change). For
/// each, the interval that applies (<see cref="ReferenceRangeSelector"/>) is compared with the one in force:
/// <list type="bullet">
/// <item>a critical low limit lowered or taken away, or a critical high limit raised or taken away, loosens;</item>
/// <item>an answer critical before that is no longer critical (or no interval applies any more) loosens;</item>
/// <item>a critical limit added, raised (low) or lowered (high), or a critical answer added, is stricter and does not.</item>
/// </list>
/// </summary>
internal static class CriticalLimitChanges
{
    private static readonly Gender[] Sexes = [Gender.Male, Gender.Female, Gender.Unknown];

    /// <summary>Each way the new intervals are looser than those in force, in words; empty when none is.</summary>
    public static IReadOnlyList<string> Loosened(IReadOnlyCollection<ReferenceRange> inForce, IReadOnlyCollection<ReferenceRange> asked, int decimals)
    {
        if (inForce.Count == 0)
        {
            return [];
        }

        var ages = inForce.Concat(asked)
            .SelectMany(r => r.AgeToDays is { } to ? new[] { r.AgeFromDays, to } : [r.AgeFromDays])
            .Append(0)
            .Distinct()
            .Order()
            .Select(a => (int?)a)
            .Prepend(null)
            .ToList();

        var found = new List<string>();
        foreach (var sex in Sexes)
        {
            foreach (var age in ages)
            {
                var before = ReferenceRangeSelector.Select(inForce, sex, age, decimals);
                if (before is null || !HasCritical(before))
                {
                    continue;
                }

                var after = ReferenceRangeSelector.Select(asked, sex, age, decimals);
                var who = Patient(sex, age);
                foreach (var change in Compare(before, after, decimals))
                {
                    var line = $"{change} ({who})";
                    if (!found.Contains(line, StringComparer.Ordinal))
                    {
                        found.Add(line);
                    }
                }
            }
        }

        return found;
    }

    private static bool HasCritical(ReferenceRange range) =>
        range.CriticalLow is not null || range.CriticalHigh is not null || range.CriticalValues.Count > 0;

    private static IEnumerable<string> Compare(ReferenceRange before, ReferenceRange? after, int decimals)
    {
        if (before.CriticalLow is { } low && (after?.CriticalLow is not { } newLow || newLow < low))
        {
            yield return $"Critical low {Number(low, decimals)} → {(after?.CriticalLow is { } l ? Number(l, decimals) : "none")}";
        }

        if (before.CriticalHigh is { } high && (after?.CriticalHigh is not { } newHigh || newHigh > high))
        {
            yield return $"Critical high {Number(high, decimals)} → {(after?.CriticalHigh is { } h ? Number(h, decimals) : "none")}";
        }

        var dropped = before.CriticalValues
            .Where(v => after is null || !after.CriticalValues.Contains(v, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (dropped.Count > 0)
        {
            yield return $"No longer critical: {string.Join(", ", dropped)}";
        }
    }

    private static string Number(decimal value, int decimals) =>
        Math.Round(value, Math.Clamp(decimals, 0, 6), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    private static string Patient(Gender sex, int? ageDays)
    {
        var who = sex switch
        {
            Gender.Male => "male",
            Gender.Female => "female",
            _ => "sex not recorded",
        };
        return ageDays switch
        {
            null => $"{who}, age not known",
            0 => $"{who}, from birth",
            { } days when days % 365 == 0 => $"{who}, from {days / 365} years",
            { } days => $"{who}, from {days} days",
        };
    }
}
