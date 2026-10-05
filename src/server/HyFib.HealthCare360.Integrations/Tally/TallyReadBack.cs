using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>
/// Compares the vouchers HC360 sent for a day with the copies Tally holds, matched by remote id. Ledger names are
/// compared after the <c>ledgerMap</c> renaming, ignoring case (Tally matches ledgers that way when it imports), and as
/// net amounts per ledger, so Tally's line order or merged lines do not count as changes. Voucher numbers are not
/// compared: Tally renumbers vouchers whose voucher type numbers itself; its number is reported instead.
/// </summary>
internal static class TallyReadBack
{
    private const int MaxLedgerDetails = 3;

    public static AccountingReadBack Compare(DateOnly date, IReadOnlyList<AccountingVoucher> expected, IReadOnlyList<TallyVoucherCopy> found, TallyImportOptions options)
    {
        var ours = found
            .Where(v => v.RemoteId is { } id && id.StartsWith(AccountingVoucher.RemoteIdPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.RemoteId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var checks = expected.Select(v => Check(v, ours, options)).ToList();
        var expectedIds = expected.Select(v => v.RemoteId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = ours
            .Where(pair => !expectedIds.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .Select(v => new AccountingVoucherFound(
                v.RemoteId![AccountingVoucher.RemoteIdPrefix.Length..], v.VoucherType, v.Number, v.Date ?? date, Money.Round(v.Amount), v.InBooks))
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .ToList();

        var differing = checks.Count(c => c.Match != AccountingVoucherMatch.Matched);
        var message = string.Create(CultureInfo.InvariantCulture,
            $"Tally holds {checks.Count(c => c.Match != AccountingVoucherMatch.Missing)} of the {checks.Count} voucher(s); {differing} differ; {others.Count} other HC360 voucher(s) on the day.");
        return new AccountingReadBack(true, message, checks, others);
    }

    private static AccountingVoucherCheck Check(AccountingVoucher voucher, Dictionary<string, List<TallyVoucherCopy>> ours, TallyImportOptions options)
    {
        if (voucher.RemoteId is not { } remoteId)
        {
            return new AccountingVoucherCheck(voucher.Key, AccountingVoucherMatch.Missing, null, null, "The voucher has no remote id, so Tally's copy cannot be found.");
        }

        if (!ours.TryGetValue(remoteId, out var copies))
        {
            return new AccountingVoucherCheck(voucher.Key, AccountingVoucherMatch.Missing, null, null, null);
        }

        // Tally keeps one voucher per remote id; if it somehow holds more, the one in the books is compared.
        var copy = copies.FirstOrDefault(c => c.InBooks) ?? copies[0];
        var amount = Money.Round(copy.Amount);
        var duplicates = copies.Count > 1 ? string.Create(CultureInfo.InvariantCulture, $" Tally holds {copies.Count} vouchers with this id.") : string.Empty;
        if (!copy.InBooks)
        {
            return new AccountingVoucherCheck(voucher.Key, AccountingVoucherMatch.NotInBooks, amount, copy.Number,
                "Cancelled or optional in Tally, so it is not in the books." + duplicates);
        }

        if (amount != Money.Round(voucher.Amount))
        {
            return new AccountingVoucherCheck(voucher.Key, AccountingVoucherMatch.AmountDiffers, amount, copy.Number,
                $"Tally holds ₹{Money.FormatInr(amount)}; HC360 sent ₹{Money.FormatInr(Money.Round(voucher.Amount))}." + duplicates);
        }

        var changes = new List<string>();
        if (copy.VoucherType is { } type && !string.Equals(type, voucher.VoucherType.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"it is a {type} voucher in Tally");
        }

        if (copy.Date is { } date && date != voucher.Date)
        {
            changes.Add($"Tally dates it {date.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture)}");
        }

        changes.AddRange(LedgerDifferences(voucher, copy, options));
        return changes.Count == 0
            ? new AccountingVoucherCheck(voucher.Key, AccountingVoucherMatch.Matched, amount, copy.Number, duplicates.Length > 0 ? duplicates.Trim() : null)
            : new AccountingVoucherCheck(voucher.Key, AccountingVoucherMatch.Changed, amount, copy.Number, Sentence(changes) + duplicates);
    }

    /// <summary>Ledgers whose net amount differs between what was sent and Tally's copy (debits positive).</summary>
    private static IEnumerable<string> LedgerDifferences(AccountingVoucher voucher, TallyVoucherCopy copy, TallyImportOptions options)
    {
        var sent = Net(voucher.Entries.Select(e => (options.MapLedger(e.LedgerName), e.Amount, e.IsDebit)));
        var held = Net(copy.Lines.Select(l => (l.Ledger, l.Amount, l.IsDebit)));
        var differences = sent.Keys.Union(held.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(ledger => sent.GetValueOrDefault(ledger) != held.GetValueOrDefault(ledger))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var ledger in differences.Take(MaxLedgerDetails))
        {
            yield return $"{ledger}: HC360 {Side(sent.GetValueOrDefault(ledger))}, Tally {Side(held.GetValueOrDefault(ledger))}";
        }

        if (differences.Count > MaxLedgerDetails)
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"{differences.Count - MaxLedgerDetails} more ledger(s) differ");
        }
    }

    private static Dictionary<string, decimal> Net(IEnumerable<(string Ledger, decimal Amount, bool IsDebit)> lines) =>
        lines.GroupBy(l => l.Ledger.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Ledger: g.Key, Net: g.Sum(l => l.IsDebit ? Money.Round(l.Amount) : -Money.Round(l.Amount))))
            .Where(l => l.Net != 0)
            .ToDictionary(l => l.Ledger, l => l.Net, StringComparer.OrdinalIgnoreCase);

    private static string Side(decimal net) => net switch
    {
        0 => "nothing",
        > 0 => "₹" + Money.FormatInr(net) + " Dr",
        _ => "₹" + Money.FormatInr(-net) + " Cr",
    };

    private static string Sentence(List<string> parts) => char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + string.Concat(parts.Skip(1).Select(p => "; " + p)) + ".";
}
