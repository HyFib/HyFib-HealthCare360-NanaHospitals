using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Card and UPI terminal reconciliation (INT-POS "reconcile"): an accountant imports the acquirer's settlement report,
/// each settled transaction is matched with HC360's card or UPI receipt (or refund), and what does not match is listed;
/// card and UPI receipts the acquirer has not settled are listed too. Each payout is a settlement journal in the Tally day
/// book of its day. Screens show document numbers, references and the card's last four digits only.
/// </summary>
public sealed class PosSettlementService(
    HospitalDbContext db,
    IAuditLogger audit,
    ICurrentUser currentUser,
    ISettingsService settings,
    TallyCorrections corrections,
    TimeProvider time)
{
    public const int MaxListDays = 92;

    /// <summary>Transactions that did not match are looked at again when they were settled within this many days.</summary>
    public const int RematchDays = 90;

    /// <summary>Receipts older than this are no longer listed as not settled.</summary>
    public const int UnsettledWindowDays = 60;

    private const int MaxUnsettled = 500;
    private const string SimulatorProvider = "simulator";

    private static readonly PosSettlementMatch[] AttentionMatches =
        [PosSettlementMatch.NoReceipt, PosSettlementMatch.AmountDiffers, PosSettlementMatch.NoRefund, PosSettlementMatch.Review];

    public async Task<PosSettlementSettings> SettingsAsync(Guid facilityId, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<PosSettlementSettings>(PosSettlementSettings.Key, facilityId, ct) ?? new PosSettlementSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed setting must not stop the reconciliation: the defaults apply until it is corrected.
            return new PosSettlementSettings();
        }
    }

    /// <summary>
    /// Imports an acquirer's settlement report for the facility: new transactions are stored (ones another report brought
    /// are skipped), matched with the facility's receipts and refunds together with earlier ones that did not match, and
    /// the Tally day books of their settlement days are queued again when already posted. Audited.
    /// </summary>
    /// <param name="settledOn">The day the acquirer paid the bank, for a report without a settlement date column.</param>
    public async Task<Result<PosSettlementImportResult>> ImportAsync(
        Guid facilityId, byte[] content, string fileName, string? acquirer, DateOnly? settledOn, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BillingDb.SignInRequired;
        }

        var name = Clip(string.IsNullOrWhiteSpace(acquirer) ? "Acquirer" : acquirer.Trim(), 64);
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        var earlier = await db.Set<PosSettlementImport>().AsNoTracking()
            .Where(i => i.FacilityId == facilityId && i.FileSha256 == sha)
            .Select(i => new { i.ImportedAt, i.ImportedByName })
            .FirstOrDefaultAsync(ct);
        if (earlier is not null)
        {
            return Error.Conflict("billing.pos_settlement_imported",
                $"This report was already imported on {BillingTime.ToLocal(earlier.ImportedAt):dd-MM-yyyy HH:mm}{(earlier.ImportedByName is { } by ? " by " + by : string.Empty)}.");
        }

        var setup = await SettingsAsync(facilityId, ct);
        var read = PosSettlementFile.Read(content, setup, settledOn);
        if (read.IsFailure)
        {
            return read.Error!;
        }

        var file = read.Value;
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in file.Rows.Select(r => r.Key).Chunk(1000))
        {
            existing.UnionWith(await db.Set<PosSettlementLine>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(l => l.FacilityId == facilityId && chunk.Contains(l.LineKey))
                .Select(l => l.LineKey)
                .ToListAsync(ct));
        }

        var now = time.GetUtcNow();
        var import = new PosSettlementImport
        {
            FacilityId = facilityId,
            Acquirer = name,
            FileName = Clip(Path.GetFileName(fileName), 200),
            FileSha256 = sha,
            FileSizeBytes = content.Length,
            Rows = file.Rows.Count,
            Ignored = file.Ignored,
            FirstSettledOn = file.Rows.Min(r => r.SettledOn),
            LastSettledOn = file.Rows.Max(r => r.SettledOn),
            ImportedAt = now,
            ImportedBy = userId,
            ImportedByName = currentUser.DisplayName ?? currentUser.UserName,
        };
        var added = file.Rows.Where(r => !existing.Contains(r.Key)).Select(r => PosSettlementLine.From(facilityId, import.Id, name, r)).ToList();
        import.Added = added.Count;
        import.AlreadyImported = file.Rows.Count - added.Count;
        db.Add(import);
        db.AddRange(added);

        var earlierOpen = await OpenLinesAsync(facilityId, ct);
        await MatchAsync(facilityId, [.. added, .. earlierOpen], setup, now, ct);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.pos.settlement_imported",
            $"Imported {name}'s card and UPI settlement report {import.FileName}: {added.Count} new of {file.Rows.Count} transactions")
        {
            EntityType = nameof(PosSettlementImport),
            EntityId = import.Id.ToString(),
            Details = new { import.FileSha256, import.Rows, import.Added, import.AlreadyImported, import.Ignored, import.FirstSettledOn, import.LastSettledOn },
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            return Busy;
        }

        foreach (var day in added.Select(l => l.SettledOn).Distinct().Order())
        {
            await corrections.RepostIfPostedAsync(facilityId, day, ct);
        }

        var matched = added.Count(l => l.Match == PosSettlementMatch.Matched);
        var toLookAt = added.Count(l => l.NeedsAttention);
        return new PosSettlementImportResult(ToDto(import), file.Columns, file.Problems, matched, toLookAt, ImportMessage(import, matched, toLookAt));
    }

    /// <summary>Matches again the transactions that did not match, for example after a missing receipt was recorded.</summary>
    public async Task<Result<PosMatchResult>> RematchAsync(Guid facilityId, CancellationToken ct)
    {
        var lines = await OpenLinesAsync(facilityId, ct);
        var before = lines.ToDictionary(l => l.Id, l => l.Match);
        await MatchAsync(facilityId, lines, await SettingsAsync(facilityId, ct), time.GetUtcNow(), ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            return Busy;
        }

        var matched = lines.Count(l => l.Match == PosSettlementMatch.Matched && before[l.Id] != PosSettlementMatch.Matched);
        var toLookAt = lines.Count(l => l.NeedsAttention);
        var message = lines.Count == 0
            ? "Every settled transaction already matches."
            : matched == 0
                ? $"None of the {lines.Count} transactions that did not match found a receipt or refund."
                : $"{Count(matched, "transaction")} now match{(matched == 1 ? "es" : string.Empty)}; {Count(toLookAt, "transaction")} still to look at.";
        return new PosMatchResult(lines.Count, matched, toLookAt, message);
    }

    /// <summary>Records that an accountant looked at a transaction that does not match, with what they found. Audited.</summary>
    public async Task<Result<PosSettlementLineDto>> ReviewAsync(Guid id, ReviewPosSettlementLineRequest request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return BillingDb.SignInRequired;
        }

        var line = await db.Set<PosSettlementLine>().FirstOrDefaultAsync(l => l.Id == id, ct);
        if (line is null)
        {
            return Error.NotFound(nameof(PosSettlementLine), id);
        }

        db.SetExpectedVersion(line, request.Version);
        var reviewed = line.Review(userId, currentUser.DisplayName ?? currentUser.UserName, request.Note, time.GetUtcNow());
        if (reviewed.IsFailure)
        {
            return reviewed.Error!;
        }

        audit.Add(new AuditEntry(AuditCategories.Data, "billing.pos.settlement_reviewed", $"Reviewed the settled card or UPI transaction {Describe(line)}: {line.ReviewNote}")
        {
            EntityType = nameof(PosSettlementLine),
            EntityId = line.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([line], ct))[0];
    }

    public async Task<PosSettlementSummaryDto> SummaryAsync(Guid facilityId, CancellationToken ct)
    {
        var setup = await SettingsAsync(facilityId, ct);
        var counts = await db.Set<PosSettlementLine>().AsNoTracking()
            .Where(l => l.FacilityId == facilityId && l.ReviewedAt == null && AttentionMatches.Contains(l.Match))
            .GroupBy(l => l.Match)
            .Select(g => new PosSettlementCountDto(g.Key, g.Count(), g.Sum(l => l.Amount)))
            .ToListAsync(ct);
        var unsettled = await UnsettledAsync(facilityId, ct);
        var lastSettledOn = await db.Set<PosSettlementLine>().AsNoTracking()
            .Where(l => l.FacilityId == facilityId)
            .MaxAsync(l => (DateOnly?)l.SettledOn, ct);
        var lastImport = await db.Set<PosSettlementImport>().AsNoTracking()
            .Where(i => i.FacilityId == facilityId)
            .OrderByDescending(i => i.ImportedAt)
            .FirstOrDefaultAsync(ct);
        return new PosSettlementSummaryDto(
            setup.EffectiveSettlementDays,
            counts.OrderBy(c => Array.IndexOf(AttentionMatches, c.Match)).ToList(),
            unsettled.Count,
            unsettled.Sum(u => u.Amount),
            lastSettledOn,
            lastImport is null ? null : ToDto(lastImport),
            setup.OtherTerminals.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList());
    }

    public async Task<PagedResult<PosSettlementImportDto>> ImportsAsync(Guid facilityId, PageRequest page, CancellationToken ct)
    {
        var result = await db.Set<PosSettlementImport>().AsNoTracking()
            .Where(i => i.FacilityId == facilityId)
            .OrderByDescending(i => i.ImportedAt)
            .ToPagedResultAsync(page, ct);
        return new PagedResult<PosSettlementImportDto>(result.Items.Select(ToDto).ToList(), result.Page, result.PageSize, result.TotalCount);
    }

    /// <summary>The payouts of a period by day: what each UTR brought to the bank and what made it up.</summary>
    public async Task<IReadOnlyList<PosSettlementDayDto>> DaysAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var facilityCode = await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Code).FirstOrDefaultAsync(ct);
        var payouts = await db.Set<PosSettlementLine>().AsNoTracking()
            .Where(l => l.FacilityId == facilityId && l.SettledOn >= from && l.SettledOn <= to)
            .GroupBy(l => new { l.SettledOn, l.Utr })
            .Select(g => new
            {
                g.Key.SettledOn,
                g.Key.Utr,
                FirstAcquirer = g.Min(l => l.Acquirer),
                LastAcquirer = g.Max(l => l.Acquirer),
                Lines = g.Count(),
                Sales = g.Sum(l => l.Type == PosSettlementLineType.Sale ? l.Amount : 0m),
                Refunds = g.Sum(l => l.Type == PosSettlementLineType.Sale ? 0m : l.Amount),
                Charges = g.Sum(l => l.Charges),
                Tax = g.Sum(l => l.Tax),
                Net = g.Sum(l => l.Net),
                ToLookAt = g.Count(l => l.ReviewedAt == null && AttentionMatches.Contains(l.Match)),
            })
            .ToListAsync(ct);
        return payouts
            .Select(p => new PosPayoutDto(
                p.SettledOn,
                p.Utr,
                p.FirstAcquirer == p.LastAcquirer ? p.FirstAcquirer ?? string.Empty : $"{p.FirstAcquirer}, {p.LastAcquirer}",
                p.Lines,
                p.Sales,
                p.Refunds,
                p.Charges,
                p.Tax,
                p.Net,
                p.ToLookAt,
                PosSettlementJournal.VoucherNumber(facilityCode, p.SettledOn, p.Utr)))
            .GroupBy(p => p.SettledOn)
            .OrderByDescending(g => g.Key)
            .Select(g => new PosSettlementDayDto(
                g.Key,
                g.Sum(p => p.Lines),
                g.Sum(p => p.Sales),
                g.Sum(p => p.Refunds),
                g.Sum(p => p.Charges),
                g.Sum(p => p.Tax),
                g.Sum(p => p.Net),
                g.Sum(p => p.ToLookAt),
                g.OrderBy(p => p.Utr, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// <summary>
    /// Settled transactions: the ones to look at (worst first), or a payout's (<paramref name="settledOn"/> with its
    /// <paramref name="utr"/>, or <paramref name="noUtr"/> for the day's lines without one).
    /// </summary>
    public async Task<PagedResult<PosSettlementLineDto>> LinesAsync(
        Guid facilityId, PageRequest page, bool attention, DateOnly? settledOn, string? utr, bool noUtr, CancellationToken ct)
    {
        var lines = db.Set<PosSettlementLine>().AsNoTracking().Where(l => l.FacilityId == facilityId);
        if (attention)
        {
            lines = lines.Where(l => l.ReviewedAt == null && AttentionMatches.Contains(l.Match));
        }

        if (settledOn is { } day)
        {
            lines = lines.Where(l => l.SettledOn == day);
        }

        if (noUtr)
        {
            lines = lines.Where(l => l.Utr == null);
        }
        else if (!string.IsNullOrWhiteSpace(utr))
        {
            var value = utr.Trim();
            lines = lines.Where(l => l.Utr == value);
        }

        var ordered = attention
            ? lines.OrderBy(l => l.Match == PosSettlementMatch.NoReceipt ? 0 : l.Match == PosSettlementMatch.AmountDiffers ? 1 : l.Match == PosSettlementMatch.NoRefund ? 2 : 3)
                .ThenByDescending(l => l.Amount).ThenBy(l => l.SettledOn).ThenBy(l => l.Id)
            : lines.OrderByDescending(l => l.SettledOn).ThenBy(l => l.TransactionAt).ThenBy(l => l.Id);
        var result = await ordered.ToPagedResultAsync(page, ct);
        return new PagedResult<PosSettlementLineDto>(await ToDtosAsync(result.Items, ct), result.Page, result.PageSize, result.TotalCount);
    }

    /// <summary>
    /// Card and UPI receipts that no settled transaction carries although the acquirer's reports already go
    /// <see cref="PosSettlementSettings.SettlementDays"/> days past them (per mode, from the first day a report covers,
    /// for the last <see cref="UnsettledWindowDays"/> days), oldest first.
    /// </summary>
    public async Task<IReadOnlyList<UnsettledPosReceiptDto>> UnsettledAsync(Guid facilityId, CancellationToken ct)
    {
        var days = (await SettingsAsync(facilityId, ct)).EffectiveSettlementDays;
        var coverage = await db.Set<PosSettlementLine>().AsNoTracking()
            .Where(l => l.FacilityId == facilityId && l.Match != PosSettlementMatch.OtherCounter)
            .GroupBy(l => l.Mode)
            .Select(g => new { Mode = g.Key, First = g.Min(l => l.TransactionOn ?? l.SettledOn), Last = g.Max(l => l.SettledOn) })
            .ToListAsync(ct);
        var today = BillingTime.Today(time);
        var rows = new List<UnsettledPosReceiptDto>();
        foreach (var mode in coverage.OrderBy(c => c.Mode))
        {
            var first = mode.First > today.AddDays(-UnsettledWindowDays) ? mode.First : today.AddDays(-UnsettledWindowDays);
            var last = mode.Last.AddDays(-days);
            if (last < first)
            {
                continue;
            }

            var (start, end) = BillingTime.Range(first, last);
            var found = await (
                    from p in db.Set<ReceiptPayment>()
                    join r in db.Set<Receipt>() on p.ReceiptId equals r.Id
                    where r.FacilityId == facilityId && p.Mode == mode.Mode && r.ReceivedAt >= start && r.ReceivedAt < end
                          && !db.Set<PosSettlementLine>().Any(l => l.ReceiptPaymentId == p.Id)
                    orderby r.ReceivedAt
                    select new { PaymentId = p.Id, ReceiptId = r.Id, r.ReceiptNumber, r.BillId, r.ReceivedAt, p.Mode, p.Amount, p.Reference, p.CardLast4, p.ExternalId })
                .Take(MaxUnsettled)
                .ToListAsync(ct);
            var transactionIds = found.Select(f => f.ExternalId).OfType<string>().ToList();
            var terminals = transactionIds.Count == 0
                ? new Dictionary<string, string>()
                : await db.Set<PosTransaction>().AsNoTracking()
                    .Where(t => transactionIds.Contains(t.TransactionId))
                    .GroupBy(t => t.TransactionId)
                    .ToDictionaryAsync(g => g.Key, g => g.First().TerminalId, ct);
            rows.AddRange(found.Select(f => new UnsettledPosReceiptDto(
                f.ReceiptId, f.ReceiptNumber, f.PaymentId, f.BillId, f.ReceivedAt, f.Mode, f.Amount, f.Reference, f.CardLast4,
                f.ExternalId is { } id ? terminals.GetValueOrDefault(id) : null,
                today.DayNumber - BillingTime.LocalDate(f.ReceivedAt).DayNumber)));
        }

        return rows.OrderBy(r => r.ReceivedAt).Take(MaxUnsettled).ToList();
    }

    /// <summary>
    /// A settlement report of the training terminal's (simulator's) approved sales of <paramref name="date"/>, paid the
    /// next day with a 1.5 % charge on cards plus GST, so the reconciliation can be practised without an acquirer. Its
    /// headings are ones HC360 reads; a day without simulator sales gives the headings only.
    /// </summary>
    public async Task<(byte[] Content, string FileName)> TrainingFileAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(date, date);
        var sales = await db.Set<PosTransaction>().AsNoTracking()
            .Where(t => t.FacilityId == facilityId && t.Provider == SimulatorProvider && t.Status == PosTransactionStatus.Approved
                        && t.CompletedAt >= start && t.CompletedAt < end)
            .OrderBy(t => t.CompletedAt)
            .ToListAsync(ct);
        var settled = date.AddDays(1).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var utr = "SIMUTR" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var csv = new StringBuilder("Settlement Date,UTR No,TID,Txn Date,Txn Type,Payment Mode,Card No,Approval Code,RRN,Txn Amount,MDR Amount,GST,Net Amount\r\n");
        foreach (var sale in sales)
        {
            var card = sale.Mode != PaymentMode.Upi;
            var mdr = card ? Money.PercentOf(sale.Amount, 1.5m) : 0m;
            var gst = Money.PercentOf(mdr, 18m);
            var at = BillingTime.ToLocal(sale.CompletedAt ?? sale.CreatedAt);
            csv.Append(CultureInfo.InvariantCulture, $"{settled},{utr},SIM{Safe(sale.TerminalId)},{at:dd/MM/yyyy HH:mm:ss},Sale,{(card ? "Card" : "UPI")},")
                .Append(CultureInfo.InvariantCulture, $"{(card ? "XXXXXXXXXXXX" + (sale.CardLast4 ?? "0000") : string.Empty)},{(card ? Safe(sale.ApprovalCode) : string.Empty)},{Safe(sale.Rrn)},")
                .Append(CultureInfo.InvariantCulture, $"{sale.Amount:0.00},{mdr:0.00},{gst:0.00},{sale.Amount - mdr - gst:0.00}\r\n");
        }

        return (Encoding.UTF8.GetBytes(csv.ToString()), $"training-card-settlement-{date:yyyy-MM-dd}.csv");

        static string Safe(string? value) => new((value ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray());
    }

    /// <summary>The facility's card and UPI payouts of the day as settlement journals, for the Tally day book.</summary>
    internal static async Task<IReadOnlyList<TallyPosSettlement>> ForDayBookAsync(
        HospitalDbContext db, Guid facilityId, DateOnly date, string? facilityCode, CancellationToken ct)
    {
        var lines = await db.Set<PosSettlementLine>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(l => l.FacilityId == facilityId && l.SettledOn == date)
            .ToListAsync(ct);
        return PosSettlementJournal.Journals(lines, date, facilityCode);
    }

    // ---------------------------------------------------------------------------------------------------- matching

    private static Error Busy => Error.Conflict("billing.pos_settlement_busy",
        "Another import or match of card and UPI settlements ran at the same time. Try again.");

    /// <summary>
    /// The facility's lines that did not find a receipt or refund and were settled recently, and those of terminals set
    /// as other counters (the list may have changed). Lines whose amount differs keep their receipt for an accountant.
    /// </summary>
    private async Task<List<PosSettlementLine>> OpenLinesAsync(Guid facilityId, CancellationToken ct)
    {
        var since = BillingTime.Today(time).AddDays(-RematchDays);
        return await db.Set<PosSettlementLine>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(l => l.FacilityId == facilityId && l.SettledOn >= since
                        && (l.Match == PosSettlementMatch.NoReceipt || l.Match == PosSettlementMatch.NoRefund || l.Match == PosSettlementMatch.OtherCounter))
            .ToListAsync(ct);
    }

    private sealed record PaymentCandidate(Guid PaymentId, Guid ReceiptId, PaymentMode Mode, decimal Amount, string? Reference, string? CardLast4, string? ExternalId, DateTimeOffset ReceivedAt)
    {
        public string? Key { get; } = PosSettlementFile.Reference(Reference, 32);
    }

    private sealed record PosApproval(string TransactionId, string ApprovalCode, decimal Amount, DateTimeOffset At);

    private sealed record RefundCandidate(Guid Id, decimal Amount, string? Reference, DateTimeOffset? PaidAt)
    {
        public string? Key { get; } = PosSettlementFile.Reference(Reference, 32);
    }

    /// <summary>
    /// Matches the lines (tracked) with the facility's card and UPI receipts and refunds. A sale matches by its RRN (the
    /// terminal's, or the one the cashier typed), else by its approval code and amount within two days (a connected
    /// terminal's sale, or the code typed as the reference), else a card payment by its last four digits and amount
    /// within a day. A refund matches the refund paid by card or UPI with its RRN or approval code. Each receipt payment
    /// and refund is matched once. Lines of other counters' terminals are set aside.
    /// </summary>
    private async Task MatchAsync(Guid facilityId, IReadOnlyList<PosSettlementLine> lines, PosSettlementSettings setup, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var line in lines)
        {
            if (setup.IsOtherTerminal(line.TerminalId))
            {
                if (line.Match != PosSettlementMatch.OtherCounter)
                {
                    line.MarkOtherCounter();
                }
            }
            else if (line.Match == PosSettlementMatch.OtherCounter)
            {
                line.Unmatch();
            }
        }

        var sales = lines.Where(l => l.Type == PosSettlementLineType.Sale && l.Match == PosSettlementMatch.NoReceipt)
            .OrderBy(l => l.TransactionAt ?? BillingTime.StartOfDay(l.SettledOn)).ThenBy(l => l.LineKey, StringComparer.Ordinal).ToList();
        if (sales.Count > 0)
        {
            await MatchSalesAsync(facilityId, sales, now, ct);
        }

        var refunds = lines.Where(l => l.Type == PosSettlementLineType.Refund && l.Match == PosSettlementMatch.NoRefund).ToList();
        if (refunds.Count > 0)
        {
            await MatchRefundsAsync(facilityId, refunds, now, ct);
        }
    }

    private async Task MatchSalesAsync(Guid facilityId, IReadOnlyList<PosSettlementLine> sales, DateTimeOffset now, CancellationToken ct)
    {
        var (start, end) = Window(sales);
        var references = Variants(sales.SelectMany(l => new[] { l.Rrn, l.ApprovalCode }));
        var codes = Variants(sales.Select(l => l.ApprovalCode));
        var last4s = sales.Select(l => l.CardLast4).OfType<string>().Distinct().ToList();

        var payments = db.Set<ReceiptPayment>().AsNoTracking()
            .Join(db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]), p => p.ReceiptId, r => r.Id, (p, r) => new { p, r })
            .Where(x => x.r.FacilityId == facilityId && (x.p.Mode == PaymentMode.Card || x.p.Mode == PaymentMode.Upi));
        var candidates = new Dictionary<Guid, PaymentCandidate>();

        foreach (var chunk in references.Chunk(1000))
        {
            AddRange(candidates, await payments.Where(x => x.p.Reference != null && chunk.Contains(x.p.Reference))
                .Select(x => new PaymentCandidate(x.p.Id, x.r.Id, x.p.Mode, x.p.Amount, x.p.Reference, x.p.CardLast4, x.p.ExternalId, x.r.ReceivedAt))
                .ToListAsync(ct));
        }

        // A reference typed with spaces, dashes or in lower case ("6279 1012-3456") is compared by its letters and
        // digits, which the database cannot do: the window's receipts with a reference are read and compared here.
        AddRange(candidates, await payments.Where(x => x.p.Reference != null && x.r.ReceivedAt >= start && x.r.ReceivedAt < end)
            .Select(x => new PaymentCandidate(x.p.Id, x.r.Id, x.p.Mode, x.p.Amount, x.p.Reference, x.p.CardLast4, x.p.ExternalId, x.r.ReceivedAt))
            .ToListAsync(ct));

        var approvals = new List<PosApproval>();
        foreach (var chunk in codes.Chunk(1000))
        {
            approvals.AddRange(await db.Set<PosTransaction>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(t => t.FacilityId == facilityId && t.Status == PosTransactionStatus.Approved && t.ApprovalCode != null && chunk.Contains(t.ApprovalCode)
                            && t.CompletedAt >= start && t.CompletedAt < end)
                .Select(t => new PosApproval(t.TransactionId, t.ApprovalCode!, t.Amount, t.CompletedAt!.Value))
                .ToListAsync(ct));
        }

        var transactionIds = approvals.Select(a => a.TransactionId).Distinct().ToList();
        foreach (var chunk in transactionIds.Chunk(1000))
        {
            AddRange(candidates, await payments.Where(x => x.p.ExternalId != null && chunk.Contains(x.p.ExternalId))
                .Select(x => new PaymentCandidate(x.p.Id, x.r.Id, x.p.Mode, x.p.Amount, x.p.Reference, x.p.CardLast4, x.p.ExternalId, x.r.ReceivedAt))
                .ToListAsync(ct));
        }

        if (last4s.Count > 0)
        {
            AddRange(candidates, await payments
                .Where(x => x.p.Mode == PaymentMode.Card && x.p.CardLast4 != null && last4s.Contains(x.p.CardLast4) && x.r.ReceivedAt >= start && x.r.ReceivedAt < end)
                .Select(x => new PaymentCandidate(x.p.Id, x.r.Id, x.p.Mode, x.p.Amount, x.p.Reference, x.p.CardLast4, x.p.ExternalId, x.r.ReceivedAt))
                .ToListAsync(ct));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var ids = candidates.Keys.ToList();
        var taken = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(1000))
        {
            taken.UnionWith(await db.Set<PosSettlementLine>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(l => l.ReceiptPaymentId != null && chunk.Contains(l.ReceiptPaymentId.Value))
                .Select(l => l.ReceiptPaymentId!.Value)
                .ToListAsync(ct));
        }

        var lookup = new PaymentLookup(candidates.Values, approvals);
        foreach (var line in sales)
        {
            var match = Find(line, lookup, taken);
            if (match is { } found)
            {
                line.MatchReceipt(found.Payment.ReceiptId, found.Payment.PaymentId, found.Payment.Amount, found.Basis, now);
                taken.Add(found.Payment.PaymentId);
            }
        }
    }

    private static void AddRange(Dictionary<Guid, PaymentCandidate> candidates, IEnumerable<PaymentCandidate> found)
    {
        foreach (var candidate in found)
        {
            candidates[candidate.PaymentId] = candidate;
        }
    }

    /// <summary>The candidates indexed the ways a settled line looks for them, oldest first within each key.</summary>
    private sealed class PaymentLookup
    {
        public PaymentLookup(IEnumerable<PaymentCandidate> payments, IEnumerable<PosApproval> approvals)
        {
            var ordered = payments.OrderBy(c => c.ReceivedAt).ThenBy(c => c.PaymentId).ToList();
            ByReference = ordered.Where(c => c.Key is not null).ToLookup(c => c.Key!, StringComparer.Ordinal);
            ByCard = ordered.Where(c => c.Mode == PaymentMode.Card && c.CardLast4 is not null).ToLookup(c => (c.CardLast4!, c.Amount));
            ByExternalId = ordered.Where(c => c.ExternalId is not null)
                .GroupBy(c => c.ExternalId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            Approvals = approvals.ToLookup(a => a.ApprovalCode.ToUpperInvariant(), StringComparer.Ordinal);
        }

        public ILookup<string, PaymentCandidate> ByReference { get; }

        public ILookup<(string Last4, decimal Amount), PaymentCandidate> ByCard { get; }

        public IReadOnlyDictionary<string, PaymentCandidate> ByExternalId { get; }

        public ILookup<string, PosApproval> Approvals { get; }
    }

    private static (PaymentCandidate Payment, PosMatchBasis Basis)? Find(PosSettlementLine line, PaymentLookup lookup, HashSet<Guid> taken)
    {
        if (line.Rrn is { } rrn
            && lookup.ByReference[rrn].Where(p => !taken.Contains(p.PaymentId)).OrderBy(p => p.Mode == line.Mode ? 0 : 1).FirstOrDefault() is { } byRrn)
        {
            return (byRrn, PosMatchBasis.Rrn);
        }

        if (line.ApprovalCode is { } code)
        {
            foreach (var approval in lookup.Approvals[code].Where(a => a.Amount == line.Amount && Near(line, a.At, 2)))
            {
                if (lookup.ByExternalId.TryGetValue(approval.TransactionId, out var payment) && !taken.Contains(payment.PaymentId))
                {
                    return (payment, PosMatchBasis.ApprovalCode);
                }
            }

            if (lookup.ByReference[code].FirstOrDefault(p => !taken.Contains(p.PaymentId) && p.Amount == line.Amount && Near(line, p.ReceivedAt, 2)) is { } typed)
            {
                return (typed, PosMatchBasis.ApprovalCode);
            }
        }

        if (line.Mode == PaymentMode.Card && line.CardLast4 is { } last4
            && lookup.ByCard[(last4, line.Amount)].FirstOrDefault(p => !taken.Contains(p.PaymentId) && Near(line, p.ReceivedAt, 1)) is { } byCard)
        {
            return (byCard, PosMatchBasis.CardAndAmount);
        }

        return null;
    }

    private async Task MatchRefundsAsync(Guid facilityId, IReadOnlyList<PosSettlementLine> lines, DateTimeOffset now, CancellationToken ct)
    {
        var references = Variants(lines.SelectMany(l => new[] { l.Rrn, l.ApprovalCode }));
        var (start, end) = Window(lines);
        var paid = db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.FacilityId == facilityId && r.Status == RefundStatus.Paid && (r.Mode == PaymentMode.Card || r.Mode == PaymentMode.Upi) && r.Reference != null);
        var found = new Dictionary<Guid, RefundCandidate>();
        foreach (var chunk in references.Chunk(1000))
        {
            foreach (var refund in await paid.Where(r => chunk.Contains(r.Reference!)).Select(r => new RefundCandidate(r.Id, r.Amount, r.Reference, r.PaidAt)).ToListAsync(ct))
            {
                found[refund.Id] = refund;
            }
        }

        // As for receipts, a reference typed with spaces, dashes or in lower case is compared by its letters and digits.
        foreach (var refund in await paid.Where(r => r.PaidAt >= start && r.PaidAt < end).Select(r => new RefundCandidate(r.Id, r.Amount, r.Reference, r.PaidAt)).ToListAsync(ct))
        {
            found[refund.Id] = refund;
        }

        if (found.Count == 0)
        {
            return;
        }

        var candidates = found.Values.OrderBy(c => c.PaidAt).ThenBy(c => c.Id).ToList();
        var ids = candidates.Select(c => c.Id).ToList();
        var taken = (await db.Set<PosSettlementLine>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(l => l.RefundId != null && ids.Contains(l.RefundId.Value))
                .Select(l => l.RefundId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        foreach (var line in lines)
        {
            var byRrn = line.Rrn is { } rrn ? candidates.FirstOrDefault(c => !taken.Contains(c.Id) && c.Key == rrn) : null;
            var byCode = byRrn is null && line.ApprovalCode is { } code
                ? candidates.FirstOrDefault(c => !taken.Contains(c.Id) && c.Key == code && c.Amount == line.Amount)
                : null;
            if ((byRrn ?? byCode) is { } refund)
            {
                line.MatchRefund(refund.Id, refund.Amount, byRrn is not null ? PosMatchBasis.Rrn : PosMatchBasis.ApprovalCode, now);
                taken.Add(refund.Id);
            }
        }
    }

    /// <summary>The instants receipts of the lines can have been taken in: from a week before the earliest settlement.</summary>
    private static (DateTimeOffset Start, DateTimeOffset End) Window(IReadOnlyList<PosSettlementLine> lines)
    {
        var first = lines.Min(l => l.TransactionOn?.AddDays(-2) ?? l.SettledOn.AddDays(-7));
        var last = lines.Max(l => l.TransactionOn?.AddDays(2) ?? l.SettledOn);
        return BillingTime.Range(first, last);
    }

    /// <summary>Whether a receipt taken at <paramref name="at"/> can be the line's transaction (within <paramref name="days"/> of it).</summary>
    private static bool Near(PosSettlementLine line, DateTimeOffset at, int days)
    {
        var day = BillingTime.LocalDate(at);
        return line.TransactionOn is { } on
            ? Math.Abs(day.DayNumber - on.DayNumber) <= days
            : day <= line.SettledOn && day >= line.SettledOn.AddDays(-7);
    }

    /// <summary>References as stored on receipts may be typed in either case.</summary>
    private static List<string> Variants(IEnumerable<string?> values) =>
        values.OfType<string>().SelectMany(v => new[] { v, v.ToLowerInvariant() }).Distinct(StringComparer.Ordinal).ToList();

    // ---------------------------------------------------------------------------------------------------- views

    private async Task<IReadOnlyList<PosSettlementLineDto>> ToDtosAsync(IReadOnlyList<PosSettlementLine> lines, CancellationToken ct)
    {
        var receiptIds = lines.Where(l => l.ReceiptId is not null).Select(l => l.ReceiptId!.Value).Distinct().ToList();
        var refundIds = lines.Where(l => l.RefundId is not null).Select(l => l.RefundId!.Value).Distinct().ToList();
        var receipts = new Dictionary<Guid, (string Number, Guid? BillId)>();
        if (receiptIds.Count > 0)
        {
            foreach (var r in await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                         .Where(r => receiptIds.Contains(r.Id))
                         .Select(r => new { r.Id, r.ReceiptNumber, r.BillId })
                         .ToListAsync(ct))
            {
                receipts[r.Id] = (r.ReceiptNumber, r.BillId);
            }
        }

        var refunds = refundIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(r => refundIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.RefundNumber, ct);
        return lines.Select(l =>
        {
            (string Number, Guid? BillId)? receipt = l.ReceiptId is { } receiptId && receipts.TryGetValue(receiptId, out var found) ? found : null;
            return new PosSettlementLineDto(
                l.Id, l.Acquirer, l.Type, l.Mode, l.TerminalId, l.TransactionAt, l.TransactionOn, l.Amount, l.Charges, l.Tax, l.Net, l.Rrn,
                l.ApprovalCode, l.CardLast4, l.SettledOn, l.Utr, l.Match, l.MatchBasis, l.RecordedAmount, l.ReceiptId, receipt?.Number,
                receipt?.BillId, l.RefundId, l.RefundId is { } refundId ? refunds.GetValueOrDefault(refundId) : null, l.NeedsAttention, l.ReviewedAt,
                l.ReviewedByName, l.ReviewNote, l.Version);
        }).ToList();
    }

    private static PosSettlementImportDto ToDto(PosSettlementImport i) => new(
        i.Id, i.Acquirer, i.FileName, i.FileSizeBytes, i.Rows, i.Added, i.AlreadyImported, i.Ignored, i.FirstSettledOn, i.LastSettledOn, i.ImportedAt,
        i.ImportedByName);

    private static string ImportMessage(PosSettlementImport import, int matched, int toLookAt)
    {
        var parts = new List<string>
        {
            import.AlreadyImported == 0
                ? $"Imported {Count(import.Added, "transaction")}."
                : $"Imported {Count(import.Added, "new transaction")}; {import.AlreadyImported} of the report's {import.Rows} were already imported.",
        };
        if (import.Added > 0)
        {
            var matches = matched == import.Added && matched > 1 ? "All match" : $"{matched} {(matched == 1 ? "matches" : "match")}";
            parts.Add(toLookAt == 0 ? $"{matches} HC360's records." : $"{matches} HC360's records; {Count(toLookAt, "transaction")} to look at.");
        }

        if (import.Ignored > 0)
        {
            parts.Add($"{Count(import.Ignored, "row")} of the report {(import.Ignored == 1 ? "was" : "were")} left out.");
        }

        return string.Join(' ', parts);
    }

    private static string Describe(PosSettlementLine line) =>
        $"{(line.Rrn is { } rrn ? "RRN " + rrn : line.ApprovalCode is { } code ? "approval " + code : "of " + line.SettledOn.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture))} "
        + $"({line.Amount.ToString("0.00", CultureInfo.InvariantCulture)})";

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count.ToString(CultureInfo.InvariantCulture)} {noun}s";

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
