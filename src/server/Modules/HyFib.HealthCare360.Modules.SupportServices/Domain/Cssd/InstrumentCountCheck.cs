namespace HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

public sealed record CountSheetLine(string ItemName, int Quantity);

/// <summary>What was counted for one instrument: received includes damaged pieces.</summary>
public sealed record CountedItem(string ItemName, int Received, int Damaged);

public sealed record ItemCountResult(string ItemName, int Expected, int Received, int Damaged)
{
    public int Missing => Math.Max(0, Expected - Received);

    public int Extra => Math.Max(0, Received - Expected);

    public bool HasDiscrepancy => Missing > 0 || Extra > 0 || Damaged > 0;
}

public sealed record CountCheckResult(
    int Expected,
    int Received,
    int Missing,
    int Extra,
    int Damaged,
    IReadOnlyList<ItemCountResult> Items,
    IReadOnlyList<string> Discrepancies)
{
    public bool HasDiscrepancy => Missing > 0 || Extra > 0 || Damaged > 0;
}

/// <summary>
/// Compares a returned tray with its count sheet. Instruments on the sheet that were not counted are treated as not
/// received, so a tray can never pass the check by omission; counted items that are not on the sheet are reported as
/// extras (usually an instrument from another tray). Names match case-insensitively.
/// </summary>
public static class InstrumentCountCheck
{
    public static CountCheckResult Compare(IReadOnlyList<CountSheetLine> countSheet, IReadOnlyList<CountedItem> counted)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var countedByName = counted
            .GroupBy(c => c.ItemName.Trim(), comparer)
            .ToDictionary(g => g.Key, g => (Received: g.Sum(c => c.Received), Damaged: g.Sum(c => c.Damaged)), comparer);
        var sheetNames = new HashSet<string>(countSheet.Select(l => l.ItemName.Trim()), comparer);

        var items = countSheet
            .GroupBy(l => l.ItemName.Trim(), comparer)
            .Select(g =>
            {
                var (received, damaged) = countedByName.TryGetValue(g.Key, out var c) ? c : (0, 0);
                return new ItemCountResult(g.First().ItemName.Trim(), g.Sum(l => l.Quantity), received, damaged);
            })
            .Concat(countedByName.Where(kv => !sheetNames.Contains(kv.Key)).Select(kv => new ItemCountResult(kv.Key, 0, kv.Value.Received, kv.Value.Damaged)))
            .ToList();

        return new CountCheckResult(
            items.Sum(i => i.Expected),
            items.Sum(i => i.Received),
            items.Sum(i => i.Missing),
            items.Sum(i => i.Extra),
            items.Sum(i => i.Damaged),
            items,
            items.Where(i => i.HasDiscrepancy).Select(Describe).ToList());
    }

    private static string Describe(ItemCountResult item)
    {
        var parts = new List<string>();
        if (item.Missing > 0)
        {
            parts.Add($"{item.Missing} missing");
        }

        if (item.Extra > 0)
        {
            parts.Add(item.Expected == 0 ? $"{item.Extra} not on the count sheet" : $"{item.Extra} extra");
        }

        if (item.Damaged > 0)
        {
            parts.Add($"{item.Damaged} damaged");
        }

        return $"{item.ItemName}: {string.Join(", ", parts)}";
    }
}
