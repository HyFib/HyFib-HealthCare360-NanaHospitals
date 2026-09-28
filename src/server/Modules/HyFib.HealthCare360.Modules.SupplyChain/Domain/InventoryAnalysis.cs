namespace HyFib.HealthCare360.Modules.SupplyChain.Domain;

public enum AbcClass
{
    A = 0,
    B,
    C,
}

public enum FsnClass
{
    Fast = 0,
    Slow,
    NonMoving,
}

/// <summary>ABC–VED matrix category: I needs the tightest control, III the least.</summary>
public enum AbcVedCategory
{
    I = 0,
    II,
    III,
}

public sealed record AbcResult(Guid ItemId, decimal Value, decimal SharePercent, decimal CumulativePercent, AbcClass Class);

public sealed record FsnResult(Guid ItemId, int IssueCount, decimal CumulativePercent, FsnClass Class);

/// <summary>
/// ABC analysis by consumption value (Pareto). Items are ranked by value; an item is A while the cumulative share
/// <em>before</em> it is below the A threshold (default 70%), B below the B threshold (default 90%), else C. Using
/// the share before the item keeps a single dominant item in A even if it alone exceeds 70%. No consumption = C.
/// </summary>
public static class AbcAnalysis
{
    public static IReadOnlyList<AbcResult> Classify(IEnumerable<(Guid ItemId, decimal Value)> items, decimal aThreshold = 70m, decimal bThreshold = 90m)
    {
        var ranked = items.Select(i => (i.ItemId, Value: Math.Max(i.Value, 0m)))
            .OrderByDescending(i => i.Value)
            .ThenBy(i => i.ItemId)
            .ToList();
        var total = ranked.Sum(i => i.Value);

        var results = new List<AbcResult>(ranked.Count);
        var cumulative = 0m;
        foreach (var (itemId, value) in ranked)
        {
            var share = total == 0 ? 0 : Math.Round(value * 100m / total, 4);
            var @class = value == 0 ? AbcClass.C
                : cumulative < aThreshold ? AbcClass.A
                : cumulative < bThreshold ? AbcClass.B
                : AbcClass.C;
            cumulative += share;
            results.Add(new AbcResult(itemId, value, share, Math.Min(cumulative, 100m), @class));
        }

        return results;
    }
}

/// <summary>
/// FSN analysis by movement frequency over the period. Items with no consumption are Non-moving; the moving items
/// are ranked by number of issue transactions and those making up the first 70% (default) of all transactions are
/// Fast, the rest Slow. Counting transactions (not quantity) avoids one bulk issue making an item look fast.
/// </summary>
public static class FsnAnalysis
{
    public static IReadOnlyList<FsnResult> Classify(IEnumerable<(Guid ItemId, int IssueCount)> items, decimal fastThreshold = 70m)
    {
        var ranked = items.Select(i => (i.ItemId, Count: Math.Max(i.IssueCount, 0)))
            .OrderByDescending(i => i.Count)
            .ThenBy(i => i.ItemId)
            .ToList();
        var total = ranked.Sum(i => i.Count);

        var results = new List<FsnResult>(ranked.Count);
        var cumulative = 0m;
        foreach (var (itemId, count) in ranked)
        {
            if (count == 0)
            {
                results.Add(new FsnResult(itemId, 0, Math.Min(cumulative, 100m), FsnClass.NonMoving));
                continue;
            }

            var @class = cumulative < fastThreshold ? FsnClass.Fast : FsnClass.Slow;
            cumulative += Math.Round(count * 100m / total, 4);
            results.Add(new FsnResult(itemId, count, Math.Min(cumulative, 100m), @class));
        }

        return results;
    }
}

/// <summary>
/// VED (Vital/Essential/Desirable) is a clinical judgement kept on the item master; combined with ABC it gives the
/// ABC–VED matrix used by hospital stores: Category I = AV, AE, AD, BV, CV; II = BE, CE, BD; III = CD.
/// </summary>
public static class VedAnalysis
{
    public static AbcVedCategory Matrix(AbcClass abc, VedClass ved) => (abc, ved) switch
    {
        (AbcClass.A, _) => AbcVedCategory.I,
        (_, VedClass.Vital) => AbcVedCategory.I,
        (AbcClass.C, VedClass.Desirable) => AbcVedCategory.III,
        _ => AbcVedCategory.II,
    };
}
