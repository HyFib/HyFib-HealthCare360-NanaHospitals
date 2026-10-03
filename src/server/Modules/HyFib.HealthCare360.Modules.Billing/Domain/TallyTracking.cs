using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>What Tally last answered for a voucher, or how the voucher got into Tally.</summary>
public enum TallyVoucherAnswer
{
    /// <summary>Not sent yet.</summary>
    None = 0,

    Created,
    Altered,

    /// <summary>Accepted in a batch whose counts did not say whether it was created or altered.</summary>
    Accepted,

    Rejected,

    /// <summary>Sent, but Tally's answer did not account for it.</summary>
    Unconfirmed,

    /// <summary>The attempt stopped before it: Tally could not be reached, or is not set up.</summary>
    NotSent,

    /// <summary>Imported into Tally by hand from the downloaded file.</summary>
    Imported,
}

/// <summary>How the voucher compared with Tally's copy at the last check against Tally.</summary>
public enum TallyMatch
{
    NotChecked = 0,
    Matched,
    Missing,
    AmountDiffers,
    Changed,

    /// <summary>Cancelled or optional in Tally.</summary>
    NotInBooks,

    /// <summary>An HC360 voucher Tally holds on the day that no facility's day book has any more.</summary>
    OnlyInTally,
}

/// <summary>Where a voucher stands, in one word for the staff screen.</summary>
public enum TallyVoucherStatus
{
    NotSent = 0,

    /// <summary>Tally confirmed it (or it was imported) and HC360's day book still has it unchanged.</summary>
    InTally,

    Refused,
    Unconfirmed,

    /// <summary>In Tally, but the day book has changed it since: post the day again.</summary>
    ChangedSinceSent,

    /// <summary>The check against Tally found it missing or different there.</summary>
    DiffersInTally,

    /// <summary>Sent before, but no longer in HC360's day book: delete it in Tally.</summary>
    NoLongerInHc360,

    /// <summary>Found in Tally only: left over from an older version of the day book.</summary>
    OnlyInTally,

    /// <summary>
    /// Found in Tally only, and it is a bill HC360 has on another day: Tally holds it as an older post left it (a
    /// cancellation posted before per-voucher tracking turned the sale into a credit note). Post the bill's day again.
    /// </summary>
    BillOfAnotherDay,
}

/// <summary>
/// One voucher of a day book as HC360 sent it to Tally (INT-TALLY): type, number, remote id, date, amount and a hash of
/// its content, with Tally's answer and the result of the last check against Tally. Rows of vouchers that left the day
/// book stay (marked) while Tally may still hold them; rows found only in Tally come from the read-back check.
/// </summary>
[NotAudited]
public sealed class TallyVoucherRecord : Entity, IVersioned
{
    public Guid ExportId { get; set; }
    public Guid FacilityId { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>The voucher's reference (<see cref="AccountingVoucher.Key"/>), unique within the day book.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The id Tally keeps for the voucher (<c>REMOTEID</c>).</summary>
    public string? RemoteId { get; set; }

    public string VoucherType { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    /// <summary>SHA-256 of the voucher as the day book has it now (empty for a voucher found only in Tally).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>False once the voucher has left HC360's day book.</summary>
    public bool InDayBook { get; set; }

    public TallyVoucherAnswer Answer { get; set; }
    public string? AnswerMessage { get; set; }
    public DateTimeOffset? AnsweredAt { get; set; }

    /// <summary>The hash of the version Tally confirmed or that was imported.</summary>
    public string? SentHash { get; set; }

    /// <summary>The post run that last confirmed it; a retry of that run does not send it again.</summary>
    public Guid? ConfirmedRunId { get; set; }

    public TallyMatch Match { get; set; }
    public decimal? TallyAmount { get; set; }
    public string? TallyNumber { get; set; }
    public string? TallyDetail { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>For a voucher only in Tally that is a bill of this facility: the day HC360 has the bill on.</summary>
    public DateOnly? BillDate { get; set; }

    /// <summary>Optimistic concurrency (<c>xmin</c>).</summary>
    public uint Version { get; set; }

    public bool IsConfirmed => Answer is TallyVoucherAnswer.Created or TallyVoucherAnswer.Altered or TallyVoucherAnswer.Accepted or TallyVoucherAnswer.Imported;

    /// <summary>Tally has the version the day book has now.</summary>
    public bool IsCurrentInTally => IsConfirmed && SentHash == Hash;

    public TallyVoucherStatus Status =>
        Match == TallyMatch.OnlyInTally
            ? (BillDate is null ? TallyVoucherStatus.OnlyInTally : TallyVoucherStatus.BillOfAnotherDay)
        : !InDayBook ? TallyVoucherStatus.NoLongerInHc360
        : Answer == TallyVoucherAnswer.Rejected ? TallyVoucherStatus.Refused
        : Answer == TallyVoucherAnswer.Unconfirmed ? TallyVoucherStatus.Unconfirmed
        : !IsConfirmed ? TallyVoucherStatus.NotSent
        : SentHash != Hash ? TallyVoucherStatus.ChangedSinceSent
        : Match is TallyMatch.Missing or TallyMatch.AmountDiffers or TallyMatch.Changed or TallyMatch.NotInBooks ? TallyVoucherStatus.DiffersInTally
        : TallyVoucherStatus.InTally;

    internal void ClearCheck()
    {
        Match = TallyMatch.NotChecked;
        TallyAmount = null;
        TallyNumber = null;
        TallyDetail = null;
        CheckedAt = null;
        BillDate = null;
    }
}

/// <summary>Records of the day book's vouchers after <see cref="TallyTracking.Track"/>.</summary>
/// <param name="Current">The record of each voucher of the day book, by key.</param>
/// <param name="Added">New records, to be added to the database.</param>
/// <param name="Removed">Records of vouchers that left the day book and never reached Tally, to be deleted.</param>
public sealed record TallyTracked(IReadOnlyDictionary<string, TallyVoucherRecord> Current, IReadOnlyList<TallyVoucherRecord> Added, IReadOnlyList<TallyVoucherRecord> Removed);

/// <summary>Counts of the day book's vouchers by where they stand.</summary>
public sealed record TallySending(int Vouchers, int InTally, int NotSent, int Refused, int ChangedSinceSent, int DiffersInTally, int NoLongerInHc360, int OnlyInTally)
{
    /// <summary>Tally has the day book as HC360 has it now, as far as HC360 knows.</summary>
    public bool Complete => InTally == Vouchers && NoLongerInHc360 == 0 && OnlyInTally == 0;
}

/// <summary>
/// The pure rules behind per-voucher tracking (INT-TALLY): fingerprints, matching the records to the day book, applying
/// Tally's answers and the read-back check, and the counts shown per day.
/// </summary>
public static class TallyTracking
{
    public const int MaxText = 500;

    /// <summary>The longest reference a voucher record keeps (the <c>key</c> column).</summary>
    public const int MaxKey = 200;

    /// <summary>SHA-256 of everything HC360 sends for the voucher, independent of the order of its lines.</summary>
    public static string Fingerprint(AccountingVoucher voucher)
    {
        var text = new StringBuilder()
            .Append(voucher.VoucherType.Trim()).Append('|')
            .Append(voucher.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('|')
            .Append(voucher.Number.Trim()).Append('|')
            .Append(voucher.Key).Append('|')
            .Append(voucher.Narration.Trim()).Append('\n');
        foreach (var entry in voucher.Entries
                     .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.LedgerName.Trim()}|{(e.IsDebit ? "Dr" : "Cr")}|{Money.Round(e.Amount):0.00}|{e.CostCentre?.Trim()}"))
                     .Order(StringComparer.Ordinal))
        {
            text.Append(entry).Append('\n');
        }

        return Hex(text.ToString());
    }

    /// <summary>Fingerprint of a whole day book: changes when any voucher is added, removed or changed.</summary>
    public static string Fingerprint(IReadOnlyList<AccountingVoucher> vouchers) =>
        Hex(string.Join('\n', vouchers.Select(v => v.Key + "|" + Fingerprint(v)).Order(StringComparer.Ordinal)));

    /// <summary>References used by more than one voucher: Tally would keep only the last of them.</summary>
    public static IReadOnlyList<string> DuplicateKeys(IReadOnlyList<AccountingVoucher> vouchers) =>
        vouchers.GroupBy(v => v.Key, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Why the day book cannot be tracked voucher by voucher, or null: two vouchers share a reference (Tally would keep
    /// only one of them), or a reference is longer than the records keep. A retry cannot fix either.
    /// </summary>
    public static string? KeyProblem(IReadOnlyList<AccountingVoucher> vouchers)
    {
        if (DuplicateKeys(vouchers) is { Count: > 0 } duplicates)
        {
            return $"Vouchers share the reference {string.Join(", ", duplicates.Take(3))}, "
                   + "so Tally would keep only one of them.";
        }

        var tooLong = vouchers.Select(v => v.Key).Where(k => k.Length > MaxKey).Order(StringComparer.Ordinal).ToList();
        return tooLong.Count == 0
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"The reference {tooLong[0][..40]}… is {tooLong[0].Length} characters long; "
                + $"HC360 tracks references of at most {MaxKey}.");
    }

    /// <summary>
    /// Brings the records in line with the day book: a record per voucher (new ones added), current type, number,
    /// amount and hash; vouchers that left the day book are marked, or their records dropped when nothing of them reached
    /// Tally. A record found only in Tally becomes an ordinary record when the day book has the voucher again. Callers
    /// that keep the records check <see cref="KeyProblem"/> first: a record's key is the voucher's whole reference.
    /// </summary>
    public static TallyTracked Track(TallyExport export, IReadOnlyList<TallyVoucherRecord> records, IReadOnlyList<AccountingVoucher> vouchers)
    {
        var byKey = records.GroupBy(r => r.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var current = new Dictionary<string, TallyVoucherRecord>(StringComparer.Ordinal);
        var added = new List<TallyVoucherRecord>();
        foreach (var voucher in vouchers)
        {
            if (!byKey.TryGetValue(voucher.Key, out var record))
            {
                record = new TallyVoucherRecord
                {
                    ExportId = export.Id,
                    FacilityId = export.FacilityId,
                    Date = export.Date,
                    Key = voucher.Key,
                };
                byKey[voucher.Key] = record;
                added.Add(record);
            }
            else if (record.Match == TallyMatch.OnlyInTally)
            {
                record.ClearCheck();
            }

            record.RemoteId = Bound(voucher.RemoteId, 210);
            record.VoucherType = Bound(voucher.VoucherType.Trim(), 32)!;
            record.Number = Bound(voucher.Number.Trim(), 100)!;
            record.Amount = Money.Round(voucher.Amount);
            record.Hash = Fingerprint(voucher);
            record.InDayBook = true;
            current[voucher.Key] = record;
        }

        var removed = new List<TallyVoucherRecord>();
        foreach (var record in records.Where(r => !current.ContainsKey(r.Key) && r.Match != TallyMatch.OnlyInTally))
        {
            if (record.IsConfirmed || record.Answer == TallyVoucherAnswer.Unconfirmed)
            {
                record.InDayBook = false;
            }
            else
            {
                removed.Add(record);
            }
        }

        return new TallyTracked(current, added, removed);
    }

    /// <summary>
    /// Whether an attempt of the run must send the voucher: everything is sent once per run, and a retry of the same run
    /// sends only what Tally has not confirmed in it yet.
    /// </summary>
    public static bool NeedsSending(TallyVoucherRecord record, Guid? runId) =>
        !(runId is not null && record.ConfirmedRunId == runId && record.IsCurrentInTally && record.Answer != TallyVoucherAnswer.Imported);

    /// <summary>
    /// Records Tally's answer for each voucher sent. A provider that answered only with counts confirms the vouchers when
    /// the counts account for all of them. A voucher not sent keeps an earlier confirmation: Tally still holds that copy.
    /// </summary>
    public static void Apply(IReadOnlyDictionary<string, TallyVoucherRecord> records, IReadOnlyList<AccountingVoucher> sent, AccountingExportResult result, Guid? runId, DateTimeOffset now)
    {
        var answers = result.Vouchers.GroupBy(a => a.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var countsOnly = result.Vouchers.Count == 0 && result.Success && result.Created + result.Altered == sent.Count;
        foreach (var voucher in sent)
        {
            if (!records.TryGetValue(voucher.Key, out var record))
            {
                continue;
            }

            var answer = answers.GetValueOrDefault(voucher.Key)
                         ?? new AccountingVoucherAnswer(voucher.Key,
                             countsOnly ? AccountingVoucherOutcome.Accepted
                             : result.NotConfigured ? AccountingVoucherOutcome.NotSent
                             : AccountingVoucherOutcome.Unconfirmed,
                             countsOnly ? null : result.Message ?? "Tally's answer did not account for this voucher.");
            if (answer.Outcome == AccountingVoucherOutcome.NotSent && record.IsConfirmed)
            {
                continue;
            }

            record.Answer = answer.Outcome switch
            {
                AccountingVoucherOutcome.Created => TallyVoucherAnswer.Created,
                AccountingVoucherOutcome.Altered => TallyVoucherAnswer.Altered,
                AccountingVoucherOutcome.Accepted => TallyVoucherAnswer.Accepted,
                AccountingVoucherOutcome.Rejected => TallyVoucherAnswer.Rejected,
                AccountingVoucherOutcome.Unconfirmed => TallyVoucherAnswer.Unconfirmed,
                _ => TallyVoucherAnswer.NotSent,
            };
            record.AnswerMessage = Bound(answer.Message, MaxText);
            record.AnsweredAt = now;
            if (record.IsConfirmed)
            {
                record.SentHash = record.Hash;
                record.ConfirmedRunId = runId;
                record.ClearCheck();
            }
        }
    }

    /// <summary>Records that the vouchers were imported from the file, so Tally holds the day book as it is now.</summary>
    public static void MarkImported(IEnumerable<TallyVoucherRecord> current, DateTimeOffset now)
    {
        foreach (var record in current)
        {
            record.Answer = TallyVoucherAnswer.Imported;
            record.AnswerMessage = null;
            record.AnsweredAt = now;
            record.SentHash = record.Hash;
            record.ConfirmedRunId = null;
            record.ClearCheck();
        }
    }

    public static TallySending Count(IEnumerable<TallyVoucherRecord> records)
    {
        var statuses = records.Select(r => r.Status).ToList();
        int Of(TallyVoucherStatus status) => statuses.Count(s => s == status);
        return new TallySending(
            statuses.Count(s => s is not (TallyVoucherStatus.NoLongerInHc360 or TallyVoucherStatus.OnlyInTally
                or TallyVoucherStatus.BillOfAnotherDay)),
            Of(TallyVoucherStatus.InTally),
            Of(TallyVoucherStatus.NotSent),
            Of(TallyVoucherStatus.Refused) + Of(TallyVoucherStatus.Unconfirmed),
            Of(TallyVoucherStatus.ChangedSinceSent),
            Of(TallyVoucherStatus.DiffersInTally),
            Of(TallyVoucherStatus.NoLongerInHc360),
            Of(TallyVoucherStatus.OnlyInTally) + Of(TallyVoucherStatus.BillOfAnotherDay));
    }

    /// <summary>The export row's counts of the day book's vouchers.</summary>
    public static void Summarize(TallyExport export, IEnumerable<TallyVoucherRecord> records)
    {
        var current = records.Where(r => r.InDayBook && r.Match != TallyMatch.OnlyInTally).ToList();
        export.VoucherCount = current.Count;
        export.ConfirmedCount = current.Count(r => r.IsCurrentInTally);
        export.RejectedCount = current.Count(r => r.Answer == TallyVoucherAnswer.Rejected);
        export.CreatedCount = current.Count(r => r.ConfirmedRunId is not null && r.ConfirmedRunId == export.RunId && r.Answer == TallyVoucherAnswer.Created);
        export.AlteredCount = current.Count(r => r.ConfirmedRunId is not null && r.ConfirmedRunId == export.RunId
                                                 && r.Answer is TallyVoucherAnswer.Altered or TallyVoucherAnswer.Accepted);
        export.ErrorCount = current.Count(r => r.Answer is TallyVoucherAnswer.Rejected or TallyVoucherAnswer.Unconfirmed);
    }

    /// <summary>
    /// Applies the read-back check to the records: each voucher of the day book gets Tally's verdict; a voucher that left
    /// the day book is dropped once Tally no longer holds it; HC360 vouchers only Tally holds (and that no other facility
    /// tracks) are recorded so staff can delete them there, except a bill HC360 has on another day, which is recorded
    /// with that day so staff post it again instead. Returns the records to add and to delete.
    /// </summary>
    /// <param name="billDays">
    /// Bills of the facility among the vouchers only in Tally, by number: the day each was finalised.
    /// </param>
    public static (IReadOnlyList<TallyVoucherRecord> Added, IReadOnlyList<TallyVoucherRecord> Removed) ApplyCheck(
        TallyExport export,
        IReadOnlyList<TallyVoucherRecord> records,
        AccountingReadBack readBack,
        ISet<string> trackedElsewhere,
        DateTimeOffset now,
        IReadOnlyDictionary<string, DateOnly>? billDays = null)
    {
        var checks = readBack.Vouchers.GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var others = readBack.Others.GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var added = new List<TallyVoucherRecord>();
        var removed = new List<TallyVoucherRecord>();
        var differences = 0;

        foreach (var record in records)
        {
            if (record.Match == TallyMatch.OnlyInTally)
            {
                removed.Add(record);
                continue;
            }

            if (!record.InDayBook)
            {
                if (others.Remove(record.Key, out var still))
                {
                    record.Match = TallyMatch.Matched;
                    record.TallyAmount = still.Amount;
                    record.TallyNumber = Bound(still.Number, 100);
                    record.TallyDetail = still.InBooks ? "Still in Tally." : "Still in Tally, cancelled or optional.";
                    record.CheckedAt = now;
                    differences++;
                }
                else
                {
                    removed.Add(record);
                }

                continue;
            }

            if (!checks.TryGetValue(record.Key, out var check))
            {
                continue;
            }

            record.Match = check.Match switch
            {
                AccountingVoucherMatch.Matched => TallyMatch.Matched,
                AccountingVoucherMatch.Missing => TallyMatch.Missing,
                AccountingVoucherMatch.AmountDiffers => TallyMatch.AmountDiffers,
                AccountingVoucherMatch.Changed => TallyMatch.Changed,
                _ => TallyMatch.NotInBooks,
            };
            record.TallyAmount = check.FoundAmount;
            record.TallyNumber = Bound(check.FoundNumber, 100);
            record.TallyDetail = Bound(check.Detail, MaxText);
            record.CheckedAt = now;
            if (record.Match != TallyMatch.Matched)
            {
                differences++;
            }
        }

        // A reference longer than a record keeps cannot be tracked; such a voucher is not HC360's.
        foreach (var other in others.Values.Where(o => !trackedElsewhere.Contains(o.Key) && o.Key.Length <= MaxKey))
        {
            var billDay = billDays is not null && billDays.TryGetValue(other.Key, out var day) && day != export.Date
                ? day
                : (DateOnly?)null;
            added.Add(new TallyVoucherRecord
            {
                ExportId = export.Id,
                FacilityId = export.FacilityId,
                Date = export.Date,
                Key = other.Key,
                RemoteId = Bound(AccountingVoucher.RemoteIdPrefix + other.Key, 210),
                VoucherType = Bound(other.VoucherType, 32) ?? string.Empty,
                Number = Bound(other.Number, 100) ?? string.Empty,
                Amount = other.Amount,
                Match = TallyMatch.OnlyInTally,
                TallyAmount = other.Amount,
                TallyNumber = Bound(other.Number, 100),
                TallyDetail = billDay is { } billed
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"Bill {other.Key} of {billed:dd-MMM-yyyy}: Tally holds it on this day as an older post left it. "
                        + $"Post {billed:dd-MMM-yyyy} again; do not delete it.")
                    : other.InBooks
                        ? "Only in Tally: no day book has this voucher any more."
                        : "Only in Tally, cancelled or optional there.",
                CheckedAt = now,
                BillDate = billDay,
            });
            differences++;
        }

        export.TallyCheck = differences == 0 ? TallyCheckStatus.Matched : TallyCheckStatus.Differences;
        export.TallyDifferences = differences;
        export.TallyCheckedAt = now;
        export.TallyCheckMessage = Bound(readBack.Message, MaxText);
        return (added, removed);
    }

    public static string? Bound(string? value, int max)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text.Length <= max ? text : text[..max];
    }

    private static string Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
