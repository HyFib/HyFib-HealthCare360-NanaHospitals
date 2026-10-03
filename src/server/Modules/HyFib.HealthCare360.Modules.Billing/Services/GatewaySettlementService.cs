using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Settlements from the payment gateway (INT-PG reconciliation). A day's settlements are read through
/// <see cref="IPaymentGateway.GetSettlementsAsync"/> (hourly by the reconciliation job for today and yesterday, and on
/// demand), stored once each, and every transaction is matched by its gateway id: payments to receipts, refunds to
/// refunds, prepaid bookings included. What does not match becomes an exception, worst first. A processed settlement
/// becomes the settlement journal of its day in the Tally day book.
/// </summary>
public sealed class GatewaySettlementService(
    HospitalDbContext db,
    IServiceProvider services,
    GatewayDiscrepancyService discrepancies,
    TallyCorrections tally,
    INotificationService notifications,
    ISettingsService settings,
    TimeProvider time,
    ILogger<GatewaySettlementService> logger)
{
    /// <summary>The longest range the settlement list answers at once.</summary>
    public const int MaxListDays = 92;

    /// <summary>
    /// Reads one day's settlements for the facility's merchant account, stores the new ones with their transactions
    /// matched, updates the status of known ones, and lists what does not match. Safe to repeat.
    /// </summary>
    public async Task<Result<SettlementFetchResult>> FetchAsync(Guid facilityId, DateOnly day, CancellationToken ct)
    {
        var today = BillingTime.Today(time);
        if (day > today)
        {
            return Error.Field("date", "Choose today or an earlier day.");
        }

        if (day < today.AddDays(-366))
        {
            return Error.Field("date", "Settlements can be fetched for the last year only.");
        }

        if (services.GetService<IPaymentGateway>() is not { } gateway)
        {
            return Error.Rule("billing.gateway_not_configured", "No payment gateway is configured.");
        }

        PaymentSettlementReport report;
        try
        {
            report = await gateway.GetSettlementsAsync(day, facilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the gateway's settlements of {Day}", day);
            return Error.Rule("billing.gateway_unavailable", "The payment gateway could not be reached. Try again shortly.");
        }

        if (!report.Available)
        {
            return Error.Rule("billing.gateway_unavailable", report.Problem ?? "The payment gateway's settlements could not be read.");
        }

        var ids = report.Settlements.Select(s => s.SettlementId).ToList();
        var stored = ids.Count == 0
            ? []
            : await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(s => s.Provider == report.Provider && ids.Contains(s.SettlementId))
                .ToDictionaryAsync(s => s.SettlementId, ct);
        var now = time.GetUtcNow();
        var added = new List<GatewaySettlement>();
        var nowProcessed = new List<GatewaySettlement>();
        foreach (var reported in report.Settlements)
        {
            if (stored.TryGetValue(reported.SettlementId, out var known))
            {
                var wasProcessed = known.Status == PaymentSettlementStatus.Processed;
                if (known.UpdateStatus(reported.Status, reported.Utr, now) && !wasProcessed && known.Status == PaymentSettlementStatus.Processed)
                {
                    nowProcessed.Add(known);
                }

                continue;
            }

            var settlement = GatewaySettlement.From(facilityId, report.Provider, reported, BillingTime.LocalDate(reported.SettledAt), now);
            await MatchAsync(settlement.Lines, ct);
            db.Add(settlement);
            added.Add(settlement);
        }

        // Settlements stored earlier whose exceptions were never raised (the run stopped, or clashed with another) or
        // that still do not match: what does not match is matched again (a receipt may have been recorded since), and
        // their exceptions are raised again, so none is lost.
        var unfinished = await UnfinishedAsync(stored.Values.Select(s => s.Id).ToList(), ct);
        var (rematched, moved) = await RematchAsync(unfinished, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another fetch of the same day stored these settlements first (unique per provider and settlement id).
            logger.LogInformation(ex, "Settlements of {Day} were stored by a concurrent fetch", day);
            db.ChangeTracker.Clear();
            return new SettlementFetchResult(day, report.Settlements.Count, 0, 0, "Another fetch stored these settlements a moment ago. Refresh to see them.");
        }

        var opened = await RaiseExceptionsAsync([.. added, .. unfinished], rematched, ct);
        if (nowProcessed.Count > 0 || added.Count > 0)
        {
            await QueueJournalsAsync(added.Concat(nowProcessed).Where(s => s.Status == PaymentSettlementStatus.Processed), ct);
        }

        await RepostAsync(moved, ct);
        foreach (var (facility, count) in opened)
        {
            // Each facility hears about its own exceptions (a shared merchant account's settlement holds several).
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, facility, "Online payments need attention",
                $"{count} transaction{(count == 1 ? string.Empty : "s")} in the gateway's settlements of "
                + $"{day:dd MMM yyyy} did not match HC360's records.",
                "/billing/reconciliation", "billing", "warning", ct);
        }

        return new SettlementFetchResult(
            day, report.Settlements.Count, added.Count, nowProcessed.Count,
            Describe(report.Settlements.Count, added.Count, opened.Values.Sum()));
    }

    /// <summary>Of these settlements, those whose exceptions were never raised or that still do not match.</summary>
    private async Task<List<GatewaySettlement>> UnfinishedAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
        ids.Count == 0
            ? []
            : await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Include(s => s.Lines)
                .Where(s => ids.Contains(s.Id)
                            && (!s.ExceptionsRaised
                                || s.Lines.Any(l => l.Match != SettlementMatch.Matched)
                                || s.Amount != s.PaymentsAmount - s.RefundsAmount + s.AdjustmentsAmount - s.Fees))
                .ToListAsync(ct);

    /// <summary>
    /// Matches the lines that do not match yet again. Returns the settlements where a line changed, and the day books
    /// to post again where a processed settlement's line moved to another facility.
    /// </summary>
    private async Task<(HashSet<Guid> Changed, List<(Guid FacilityId, DateOnly Date)> Moved)> RematchAsync(
        IReadOnlyCollection<GatewaySettlement> settlements, CancellationToken ct)
    {
        var open = settlements
            .SelectMany(s => s.Lines.Where(l => l.Match != SettlementMatch.Matched)
                .Select(l => (Settlement: s, Line: l)))
            .ToList();
        if (open.Count == 0)
        {
            return ([], []);
        }

        static (SettlementMatch, Guid, Guid?, Guid?) Outcome(GatewaySettlementLine l) =>
            (l.Match, l.FacilityId, l.ReceiptId, l.RefundId);
        var before = open.ToDictionary(o => o.Line.Id, o => Outcome(o.Line));
        await MatchAsync(open.Select(o => o.Line).ToList(), ct);
        var changed = open.Where(o => before[o.Line.Id] != Outcome(o.Line)).ToList();
        return (
            changed.Select(o => o.Settlement.Id).ToHashSet(),
            changed.Where(o => o.Settlement.Status == PaymentSettlementStatus.Processed)
                .SelectMany(o => Moved(before[o.Line.Id].Item2, o.Line.FacilityId, o.Settlement.SettledOn))
                .Distinct()
                .ToList());
    }

    /// <summary>Both day books of a settlement day when a line moved from one facility to another.</summary>
    private static (Guid FacilityId, DateOnly Date)[] Moved(Guid from, Guid to, DateOnly day) =>
        from == to ? [] : [(from, day), (to, day)];

    /// <summary>
    /// Matches settled transactions to HC360 by gateway id: a payment to the receipt that recorded it, a refund to the
    /// refund that sent it. A line takes the facility of what it matched; money nothing records takes the facility of
    /// its order or prepaid booking, so it shows up where it was taken.
    /// </summary>
    internal async Task MatchAsync(IReadOnlyCollection<GatewaySettlementLine> lines, CancellationToken ct)
    {
        var paymentIds = lines.Where(l => l.Type == PaymentTransactionType.Payment).Select(l => l.EntityId).Distinct().ToList();
        var refundIds = lines.Where(l => l.Type == PaymentTransactionType.Refund).Select(l => l.EntityId).Distinct().ToList();
        var relatedPayments = paymentIds.Concat(lines.Where(l => l.PaymentId is not null).Select(l => l.PaymentId!)).Distinct().ToList();
        var orderIds = lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!).Distinct().ToList();

        var receipts = paymentIds.Count == 0
            ? []
            : await (from p in db.Set<ReceiptPayment>()
                     join r in db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.ReceiptId equals r.Id
                     where p.Mode == PaymentMode.PaymentGateway && p.ExternalId != null && paymentIds.Contains(p.ExternalId)
                     select new { PaymentId = p.ExternalId!, ReceiptId = r.Id, r.FacilityId, p.Amount })
                .ToListAsync(ct);
        var refunds = refundIds.Count == 0
            ? []
            : await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(r => r.GatewayRefundId != null && refundIds.Contains(r.GatewayRefundId))
                .Select(r => new { RefundId = r.Id, GatewayRefundId = r.GatewayRefundId!, r.FacilityId, r.Amount })
                .ToListAsync(ct);
        var targets = await FindTargetsAsync(relatedPayments, orderIds, ct);

        foreach (var line in lines)
        {
            if (line.Type == PaymentTransactionType.Payment && receipts.FirstOrDefault(r => r.PaymentId == line.EntityId) is { } receipt)
            {
                line.MatchTo(receipt.ReceiptId, null, receipt.Amount, receipt.FacilityId);
            }
            else if (line.Type == PaymentTransactionType.Refund && refunds.FirstOrDefault(r => r.GatewayRefundId == line.EntityId) is { } refund)
            {
                line.MatchTo(null, refund.RefundId, refund.Amount, refund.FacilityId);
            }
            else
            {
                line.Unmatched(targets.For(line)?.FacilityId);
            }
        }
    }

    /// <summary>
    /// A receipt was just recorded for an online payment: any settled transaction carrying its id matches it now, and
    /// its exceptions follow (cleared when the amounts agree, an amount difference when they do not). Staged in the
    /// caller's unit of work. Returns the day books to post again once that is saved (<see cref="RepostAsync"/>): a
    /// processed settlement's journal moves to the receipt's facility when the transaction had been kept under another.
    /// </summary>
    internal async Task<IReadOnlyList<(Guid FacilityId, DateOnly Date)>> RematchPaymentAsync(
        string paymentId, CancellationToken ct)
    {
        var lines = await db.Set<GatewaySettlementLine>()
            .Where(l => l.EntityId == paymentId && l.Type == PaymentTransactionType.Payment)
            .Where(l => l.Match != SettlementMatch.Matched)
            .ToListAsync(ct);
        if (lines.Count == 0)
        {
            return [];
        }

        var before = lines.ToDictionary(l => l.Id, l => l.FacilityId);
        await MatchAsync(lines, ct);
        var settlementIds = lines.Select(l => l.GatewaySettlementId).Distinct().ToList();
        var settlements = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AsNoTracking()
            .Where(s => settlementIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);
        var targets = await TargetsForAsync(lines, ct);
        foreach (var line in lines)
        {
            await RaiseForLineAsync(settlements[line.GatewaySettlementId], line, targets, new HashSet<string>(), ct);
        }

        return lines
            .Where(l => settlements[l.GatewaySettlementId].Status == PaymentSettlementStatus.Processed)
            .SelectMany(l => Moved(before[l.Id], l.FacilityId, settlements[l.GatewaySettlementId].SettledOn))
            .Distinct()
            .ToList();
    }

    /// <summary>Puts day books that may already be in Tally back in its queue.</summary>
    internal async Task RepostAsync(IEnumerable<(Guid FacilityId, DateOnly Date)> days, CancellationToken ct)
    {
        foreach (var (facility, date) in days.Distinct())
        {
            await tally.RepostIfPostedAsync(facility, date, ct);
        }
    }

    /// <summary>
    /// Online receipts that no settlement has paid to the bank within the expected days, listed once the gateway has
    /// settled something newer (until then settlements are simply not being read yet). Only the last month is looked at.
    /// </summary>
    internal async Task<int> FindUnsettledReceiptsAsync(Guid facilityId, CancellationToken ct)
    {
        var reconciliation = await GatewayReconciliationSettings.ReadAsync(settings, facilityId, ct);
        if (!reconciliation.Enabled)
        {
            return 0;
        }

        var latest = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(s => s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId))
            .MaxAsync(s => (DateTimeOffset?)s.SettledAt, ct);
        if (latest is null)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var days = reconciliation.EffectiveSettlementDays;
        var before = (latest.Value < now ? latest.Value : now).AddDays(-days);
        var since = now.AddDays(-(days + 30));
        var unsettled = await (
                from r in db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                from p in r.Payments
                where r.FacilityId == facilityId && r.ReceivedAt >= since && r.ReceivedAt < before
                      && p.Mode == PaymentMode.PaymentGateway && p.ExternalId != null
                      && !db.Set<GatewaySettlementLine>().Any(l => l.EntityId == p.ExternalId)
                orderby r.ReceivedAt
                select new { r.Id, r.ReceiptNumber, r.BillId, r.IpAccountId, r.ReceivedAt, PaymentId = p.ExternalId!, p.Amount })
            .Take(500)
            .ToListAsync(ct);

        var opened = 0;
        foreach (var receipt in unsettled)
        {
            var exception = await discrepancies.RaiseAsync(
                DiscrepancyKind.UnsettledReceipt, receipt.PaymentId, facilityId,
                $"Receipt {receipt.ReceiptNumber} (₹{Money.FormatInr(receipt.Amount)}, {receipt.PaymentId}) is not in any settlement {days} days after the payment.",
                d =>
                {
                    d.ReceiptId = receipt.Id;
                    d.BillId = receipt.BillId;
                    d.IpAccountId = receipt.IpAccountId;
                    d.RecordedAmount = receipt.Amount;
                    d.GatewayAt = receipt.ReceivedAt;
                },
                ct);
            opened += db.Entry(exception).State == EntityState.Added ? 1 : 0;
        }

        // Saved together; after a clash with a concurrent run the next run raises them again.
        return await SaveExceptionsAsync(ct) ? opened : 0;
    }

    /// <summary>
    /// The settlement journals of one facility's day for the Tally day book: each processed settlement of that day, or
    /// the facility's part of one its merchant account shares with other facilities.
    /// </summary>
    internal static async Task<IReadOnlyList<TallyGatewaySettlement>> ForDayBookAsync(HospitalDbContext db, Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var settlements = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(s => s.Lines)
            .Where(s => s.SettledOn == date && s.Status == PaymentSettlementStatus.Processed
                        && (s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId)))
            .OrderBy(s => s.SettledAt)
            .ToListAsync(ct);
        return settlements
            .SelectMany(s => SettlementJournal.Portions(s)
                .Where(p => p.FacilityId == facilityId && (p.Net != 0 || p.Charges != 0 || p.Tax != 0))
                .Select(p => new TallyGatewaySettlement(SettlementJournal.VoucherNumber(s, p), date, s.SettlementId, s.Utr, p.Net, p.Charges, p.Tax)))
            .ToList();
    }

    /// <summary>
    /// The settlements of a period by day, with totals, for the facility: its own in full, and of those another
    /// facility holds (a shared merchant account) only its part, so its totals are its own money.
    /// </summary>
    public async Task<IReadOnlyList<SettlementDayDto>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.SettledOn >= from && s.SettledOn <= to && (s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId)))
            .OrderByDescending(s => s.SettledAt)
            .Select(s => new { Settlement = s, Lines = s.Lines.Count, Unmatched = s.Lines.Count(l => l.Match != SettlementMatch.Matched) })
            .Take(1000)
            .ToListAsync(ct);
        var shared = rows.Where(r => r.Settlement.FacilityId != facilityId).Select(r => r.Settlement.Id).ToList();
        var ownLines = shared.Count == 0
            ? []
            : await db.Set<GatewaySettlementLine>().AsNoTracking()
                .Where(l => shared.Contains(l.GatewaySettlementId) && l.FacilityId == facilityId)
                .ToListAsync(ct);
        return rows
            .Select(r => r.Settlement.FacilityId == facilityId
                ? ToDto(r.Settlement, r.Lines, r.Unmatched)
                : ShareDto(r.Settlement, ownLines.Where(l => l.GatewaySettlementId == r.Settlement.Id).ToList()))
            .GroupBy(s => s.SettledOn)
            .OrderByDescending(g => g.Key)
            .Select(g => new SettlementDayDto(
                g.Key,
                g.Count(),
                g.Sum(s => s.PaymentsAmount),
                g.Sum(s => s.RefundsAmount),
                g.Sum(s => s.AdjustmentsAmount),
                g.Sum(s => s.Charges),
                g.Sum(s => s.Tax),
                g.Sum(s => s.Amount),
                g.Sum(s => s.Unmatched),
                g.ToList()))
            .ToList();
    }

    /// <summary>
    /// A settlement and its transactions. Another facility sharing the account sees only its own transactions, and
    /// their totals.
    /// </summary>
    public async Task<GatewaySettlementDetail?> GetAsync(Guid facilityId, Guid id, CancellationToken ct)
    {
        var settlement = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id && (s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId)), ct);
        if (settlement is null)
        {
            return null;
        }

        var owner = settlement.FacilityId == facilityId;
        var lines = settlement.Lines
            .Where(l => owner || l.FacilityId == facilityId)
            .OrderBy(l => l.Match == SettlementMatch.Matched)
            .ThenBy(l => l.TransactionAt)
            .ToList();
        var receiptIds = lines.Where(l => l.ReceiptId is not null).Select(l => l.ReceiptId!.Value).ToList();
        var refundIds = lines.Where(l => l.RefundId is not null).Select(l => l.RefundId!.Value).ToList();
        var receipts = receiptIds.Count == 0 ? [] : await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.ReceiptNumber, ct);
        var refunds = refundIds.Count == 0 ? [] : await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => refundIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.RefundNumber, ct);

        return new GatewaySettlementDetail(
            owner
                ? ToDto(
                    settlement, settlement.Lines.Count, settlement.Lines.Count(l => l.Match != SettlementMatch.Matched))
                : ShareDto(settlement, lines),
            lines.Select(l => new SettlementLineDto(
                    l.Id, l.Type, l.EntityId, l.PaymentId, l.OrderId, l.Method, l.TransactionAt, l.Amount, l.Fee, l.Tax, l.Net, l.Match, l.RecordedAmount,
                    l.ReceiptId, l.ReceiptId is { } receipt ? receipts.GetValueOrDefault(receipt) : null,
                    l.RefundId, l.RefundId is { } refund ? refunds.GetValueOrDefault(refund) : null))
                .ToList());
    }

    internal static GatewaySettlementDto ToDto(GatewaySettlement s, int lines, int unmatched) => new(
        s.Id, s.Provider, s.SettlementId, s.Utr, s.Status, s.SettledAt, s.SettledOn, s.PaymentsAmount, s.RefundsAmount, s.AdjustmentsAmount,
        s.Fees - s.Tax, s.Tax, s.Amount, s.Difference, lines, unmatched, false);

    /// <summary>
    /// A facility's part of a settlement another facility holds: the totals of its own transactions only, as in its
    /// settlement journal (<see cref="SettlementJournal.Portions"/>). A difference stays with the facility holding it.
    /// </summary>
    internal static GatewaySettlementDto ShareDto(
        GatewaySettlement s, IReadOnlyCollection<GatewaySettlementLine> own)
    {
        var adjustments = own
            .Where(l => l.Type is not (PaymentTransactionType.Payment or PaymentTransactionType.Refund));
        return new(
            s.Id, s.Provider, s.SettlementId, s.Utr, s.Status, s.SettledAt, s.SettledOn,
            own.Where(l => l.Type == PaymentTransactionType.Payment).Sum(l => l.Gross),
            -own.Where(l => l.Type == PaymentTransactionType.Refund).Sum(l => l.Gross),
            adjustments.Sum(l => l.Gross),
            own.Sum(l => l.Fee - l.Tax),
            own.Sum(l => l.Tax),
            own.Sum(l => l.Net),
            0m,
            own.Count,
            own.Count(l => l.Match != SettlementMatch.Matched),
            true);
    }

    /// <summary>
    /// Opens an exception for every transaction that does not match, and clears the ones a match settles: every line of
    /// a settlement whose exceptions were never raised (or of one in <paramref name="rematched"/>), else only the lines
    /// that still do not match. Saves; a clash with a concurrent run (a webhook raising the same exception) is retried
    /// with the exceptions read again, and only once the exceptions are saved is the settlement marked as raised, so a
    /// run that fails is made up by the next fetch. Returns the exceptions opened, by facility.
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, int>> RaiseExceptionsAsync(
        IReadOnlyCollection<GatewaySettlement> settlements, IReadOnlySet<Guid>? rematched, CancellationToken ct)
    {
        if (settlements.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        for (var attempt = 1; ; attempt++)
        {
            var opened = await StageExceptionsAsync(settlements, rematched, ct);
            if (await SaveExceptionsAsync(ct))
            {
                foreach (var settlement in settlements.Where(s => !s.ExceptionsRaised))
                {
                    settlement.ExceptionsRaised = true;
                }

                await db.SaveChangesAsync(ct);
                return opened.GroupBy(d => d.FacilityId).ToDictionary(g => g.Key, g => g.Count());
            }

            if (attempt == 3)
            {
                logger.LogWarning(
                    "Gateway exceptions of {Count} settlements could not be saved; the next fetch raises them again",
                    settlements.Count);
                return new Dictionary<Guid, int>();
            }
        }
    }

    /// <summary>Stages the exceptions of <see cref="RaiseExceptionsAsync"/>; returns the ones it opened.</summary>
    private async Task<List<GatewayDiscrepancy>> StageExceptionsAsync(
        IReadOnlyCollection<GatewaySettlement> settlements, IReadOnlySet<Guid>? rematched, CancellationToken ct)
    {
        var work = settlements
            .Select(s => (Settlement: s, Lines: !s.ExceptionsRaised || rematched?.Contains(s.Id) == true
                ? s.Lines.ToList()
                : s.Lines.Where(l => l.Match != SettlementMatch.Matched).ToList()))
            .ToList();
        var lines = work.SelectMany(w => w.Lines).ToList();
        var targets = await TargetsForAsync(lines, ct);

        // A refund the gateway failed matches its settled line all the same; its exception (the patient has not got the
        // money back) is the refund follow-up's to clear, never the match's.
        var refundIds = lines
            .Where(l => l is { Type: PaymentTransactionType.Refund, Match: SettlementMatch.Matched })
            .Select(l => l.EntityId)
            .Distinct()
            .ToList();
        var failedRefunds = refundIds.Count == 0
            ? []
            : (await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(r => r.GatewayRefundId != null && refundIds.Contains(r.GatewayRefundId))
                .Where(r => r.GatewayStatus == PaymentRefundStatus.Failed)
                .Select(r => r.GatewayRefundId!)
                .ToListAsync(ct))
            .ToHashSet();

        var opened = new List<GatewayDiscrepancy>();
        foreach (var (settlement, settlementLines) in work)
        {
            foreach (var line in settlementLines)
            {
                var exception = await RaiseForLineAsync(settlement, line, targets, failedRefunds, ct);
                if (exception is not null && db.Entry(exception).State == EntityState.Added)
                {
                    opened.Add(exception);
                }
            }

            if (settlement.Difference != 0)
            {
                var exception = await discrepancies.RaiseAsync(
                    DiscrepancyKind.AmountMismatch, settlement.SettlementId, settlement.FacilityId,
                    $"Settlement {settlement.SettlementId} paid ₹{Money.FormatInr(settlement.Amount)} to the bank, but its transactions come to ₹{Money.FormatInr(settlement.Amount - settlement.Difference)}.",
                    d =>
                    {
                        d.GatewayAmount = settlement.Amount;
                        d.RecordedAmount = settlement.Amount - settlement.Difference;
                        d.GatewayAt = settlement.SettledAt;
                    },
                    ct);
                if (db.Entry(exception).State == EntityState.Added)
                {
                    opened.Add(exception);
                }
            }
        }

        return opened;
    }

    /// <summary>Raises or clears the exception of one line; returns the exception it raised.</summary>
    private async Task<GatewayDiscrepancy?> RaiseForLineAsync(
        GatewaySettlement settlement, GatewaySettlementLine line, SettlementTargets targets,
        IReadOnlySet<string> failedRefunds, CancellationToken ct)
    {
        var target = targets.For(line);
        GatewayDiscrepancy? exception = null;
        switch (line)
        {
            case { Type: PaymentTransactionType.Payment, Match: SettlementMatch.Matched }:
                await discrepancies.ClearAsync(DiscrepancyKind.UnsettledReceipt, line.EntityId, $"Settled in {settlement.SettlementId} on {settlement.SettledOn:dd MMM yyyy}.", ct);
                await discrepancies.ClearAsync(DiscrepancyKind.UnrecordedPayment, line.EntityId, "The receipt was recorded.", ct);
                await discrepancies.ClearAsync(
                    DiscrepancyKind.AmountMismatch, line.EntityId, "The receipt records the settled amount.", ct);
                break;

            case { Type: PaymentTransactionType.Payment, Match: SettlementMatch.NoReceipt }:
                exception = await discrepancies.RaiseAsync(
                    DiscrepancyKind.UnrecordedPayment, line.EntityId, line.FacilityId,
                    $"₹{Money.FormatInr(line.Amount)} was paid online ({line.EntityId}) and settled in {settlement.SettlementId}, but no receipt records it. The patient has not been credited.",
                    d =>
                    {
                        d.OrderId = line.OrderId ?? target?.OrderId;
                        d.GatewayOrderRecordId = target?.GatewayOrderRecordId;
                        d.AppointmentId = target?.AppointmentId;
                        d.BillId = target?.BillId;
                        d.IpAccountId = target?.IpAccountId;
                        d.SettlementLineId = line.Id;
                        d.GatewayAmount = line.Amount;
                        d.GatewayAt = line.TransactionAt;
                    },
                    ct);
                break;

            case { Type: PaymentTransactionType.Payment, Match: SettlementMatch.AmountDiffers }:
                await discrepancies.ClearAsync(DiscrepancyKind.UnsettledReceipt, line.EntityId, $"Settled in {settlement.SettlementId}, with a different amount.", ct);
                exception = await discrepancies.RaiseAsync(
                    DiscrepancyKind.AmountMismatch, line.EntityId, line.FacilityId,
                    $"The gateway settled ₹{Money.FormatInr(line.Amount)} for {line.EntityId}, but its receipt records ₹{Money.FormatInr(line.RecordedAmount ?? 0m)}.",
                    d =>
                    {
                        d.ReceiptId = line.ReceiptId;
                        d.SettlementLineId = line.Id;
                        d.RecordedAmount = line.RecordedAmount;
                        d.GatewayAmount = line.Amount;
                        d.GatewayAt = line.TransactionAt;
                    },
                    ct);
                break;

            case { Type: PaymentTransactionType.Refund, Match: SettlementMatch.Matched }:
                if (!failedRefunds.Contains(line.EntityId))
                {
                    await discrepancies.ClearAsync(
                        DiscrepancyKind.RefundMismatch, line.EntityId, "The gateway's refund matches HC360's.", ct);
                }

                break;

            case { Type: PaymentTransactionType.Refund }:
                exception = await discrepancies.RaiseAsync(
                    DiscrepancyKind.RefundMismatch, line.EntityId, line.FacilityId,
                    line.Match == SettlementMatch.NoRefund
                        ? $"The gateway returned ₹{Money.FormatInr(line.Amount)} to a patient ({line.EntityId}, on {line.PaymentId ?? "an unknown payment"}), but no HC360 refund records it."
                        : $"The gateway refunded ₹{Money.FormatInr(line.Amount)} ({line.EntityId}), but HC360's refund records ₹{Money.FormatInr(line.RecordedAmount ?? 0m)}.",
                    d =>
                    {
                        d.RefundId = line.RefundId;
                        d.OrderId = line.OrderId;
                        d.GatewayOrderRecordId = target?.GatewayOrderRecordId;
                        d.AppointmentId = target?.AppointmentId;
                        d.BillId = target?.BillId;
                        d.SettlementLineId = line.Id;
                        d.RecordedAmount = line.RecordedAmount;
                        d.GatewayAmount = line.Amount;
                        d.GatewayAt = line.TransactionAt;
                    },
                    ct);
                break;

            default:
                exception = await discrepancies.RaiseAsync(
                    DiscrepancyKind.AmountMismatch, line.EntityId, line.FacilityId,
                    $"The gateway {(line.Gross < 0 ? "took back" : "added")} ₹{Money.FormatInr(Math.Abs(line.Gross))} in settlement {settlement.SettlementId} ({line.Type.ToString().ToLowerInvariant()} {line.EntityId}).",
                    d =>
                    {
                        d.SettlementLineId = line.Id;
                        d.GatewayAmount = line.Gross;
                        d.GatewayAt = line.TransactionAt;
                    },
                    ct);
                break;
        }

        return exception;
    }

    /// <summary>
    /// Exceptions are saved on their own: a clash with a concurrent run must not undo the settlement already stored.
    /// After a clash the exceptions staged are let go (the others in the unit of work stay), to be read again.
    /// </summary>
    private async Task<bool> SaveExceptionsAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Gateway exceptions were changed by a concurrent run; they are read again");
            var staged = db.ChangeTracker.Entries<GatewayDiscrepancy>()
                .Where(e => e.State != EntityState.Unchanged)
                .ToList();
            foreach (var entry in staged)
            {
                entry.State = EntityState.Detached;
            }

            return false;
        }
    }

    /// <summary>Settlement journals for days that may already be in Tally go back in its queue (each facility its own part).</summary>
    private async Task QueueJournalsAsync(IEnumerable<GatewaySettlement> processed, CancellationToken ct)
    {
        var settlements = processed.ToList();
        var ids = settlements.Select(s => s.Id).ToList();
        var lineFacilities = ids.Count == 0
            ? []
            : await db.Set<GatewaySettlementLine>().AsNoTracking()
                .Where(l => ids.Contains(l.GatewaySettlementId))
                .Select(l => new { l.GatewaySettlementId, l.FacilityId })
                .Distinct()
                .ToListAsync(ct);
        var days = settlements
            .SelectMany(s => lineFacilities
                .Where(l => l.GatewaySettlementId == s.Id)
                .Select(l => l.FacilityId)
                .Append(s.FacilityId)
                .Select(f => (f, s.SettledOn)))
            .ToList();
        await RepostAsync(days, ct);
    }

    /// <summary>Where the money of these lines was meant to go (see <see cref="FindTargetsAsync"/>).</summary>
    private Task<SettlementTargets> TargetsForAsync(
        IReadOnlyCollection<GatewaySettlementLine> lines, CancellationToken ct) =>
        FindTargetsAsync(
            lines.Select(l => l.Type == PaymentTransactionType.Payment ? l.EntityId : l.PaymentId)
                .OfType<string>()
                .Distinct()
                .ToList(),
            lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!).Distinct().ToList(),
            ct);

    /// <summary>Where unmatched money was meant to go: the payment link or checkout, or the prepaid booking, that took it.</summary>
    private async Task<SettlementTargets> FindTargetsAsync(IReadOnlyList<string> paymentIds, IReadOnlyList<string> orderIds, CancellationToken ct)
    {
        if (paymentIds.Count == 0 && orderIds.Count == 0)
        {
            return new SettlementTargets([], []);
        }

        var orders = await db.Set<GatewayOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(o => (o.GatewayPaymentId != null && paymentIds.Contains(o.GatewayPaymentId)) || orderIds.Contains(o.OrderId))
            .Select(o => new SettlementTarget(o.FacilityId, o.OrderId, o.GatewayPaymentId, o.Id, null, o.BillId, o.IpAccountId))
            .ToListAsync(ct);
        var bookings = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => (a.GatewayOrderId != null && orderIds.Contains(a.GatewayOrderId)) || (a.GatewayPaymentId != null && paymentIds.Contains(a.GatewayPaymentId)))
            .Select(a => new SettlementTarget(a.FacilityId, a.GatewayOrderId, a.GatewayPaymentId, null, a.Id, null, null))
            .ToListAsync(ct);
        return new SettlementTargets(orders, bookings);
    }

    private static string Describe(int settlements, int added, int opened) => settlements switch
    {
        0 => "The gateway paid nothing to the bank on this day.",
        _ when added == 0 => $"{settlements} settlement{(settlements == 1 ? " was" : "s were")} already here.",
        _ when opened == 0 => $"{added} new settlement{(added == 1 ? string.Empty : "s")}; every transaction matches.",
        _ => $"{added} new settlement{(added == 1 ? string.Empty : "s")}; {opened} transaction{(opened == 1 ? " needs" : "s need")} attention.",
    };

    private sealed record SettlementTarget(
        Guid FacilityId, string? OrderId, string? PaymentId, Guid? GatewayOrderRecordId, Guid? AppointmentId, Guid? BillId, Guid? IpAccountId);

    private sealed record SettlementTargets(IReadOnlyList<SettlementTarget> Orders, IReadOnlyList<SettlementTarget> Bookings)
    {
        public SettlementTarget? For(GatewaySettlementLine line)
        {
            var paymentId = line.Type == PaymentTransactionType.Payment ? line.EntityId : line.PaymentId;
            return Orders.FirstOrDefault(o => paymentId is not null && o.PaymentId == paymentId)
                ?? Bookings.FirstOrDefault(b => paymentId is not null && b.PaymentId == paymentId)
                ?? Orders.FirstOrDefault(o => line.OrderId is not null && o.OrderId == line.OrderId)
                ?? Bookings.FirstOrDefault(b => line.OrderId is not null && b.OrderId == line.OrderId);
        }
    }
}
