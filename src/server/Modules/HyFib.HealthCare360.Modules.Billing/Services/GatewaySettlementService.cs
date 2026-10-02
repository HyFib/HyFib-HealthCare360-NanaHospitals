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

        var opened = await RaiseExceptionsAsync(added, ct);
        if (nowProcessed.Count > 0 || added.Count > 0)
        {
            await QueueJournalsAsync(added.Concat(nowProcessed).Where(s => s.Status == PaymentSettlementStatus.Processed), ct);
        }

        if (opened > 0)
        {
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, facilityId, "Online payments need attention",
                $"{opened} transaction{(opened == 1 ? string.Empty : "s")} in the gateway's settlements of {day:dd MMM yyyy} did not match HC360's records.",
                "/billing/reconciliation", "billing", "warning", ct);
        }

        return new SettlementFetchResult(day, report.Settlements.Count, added.Count, nowProcessed.Count, Describe(report.Settlements.Count, added.Count, opened));
    }

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
    /// A receipt was just recorded for an online payment: any settled transaction carrying its id matches it now.
    /// Staged in the caller's unit of work.
    /// </summary>
    internal async Task RematchPaymentAsync(string paymentId, CancellationToken ct)
    {
        var lines = await db.Set<GatewaySettlementLine>()
            .Where(l => l.EntityId == paymentId && l.Type == PaymentTransactionType.Payment && l.Match != SettlementMatch.Matched)
            .ToListAsync(ct);
        if (lines.Count > 0)
        {
            await MatchAsync(lines, ct);
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

        await SaveExceptionsAsync(ct);
        return opened;
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

    /// <summary>The settlements of a period by day, with totals, for the facility (its own and those holding its money).</summary>
    public async Task<IReadOnlyList<SettlementDayDto>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.SettledOn >= from && s.SettledOn <= to && (s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId)))
            .OrderByDescending(s => s.SettledAt)
            .Select(s => new { Settlement = s, Lines = s.Lines.Count, Unmatched = s.Lines.Count(l => l.Match != SettlementMatch.Matched) })
            .Take(1000)
            .ToListAsync(ct);
        return rows
            .Select(r => ToDto(r.Settlement, r.Lines, r.Unmatched))
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

    /// <summary>A settlement and its transactions. Another facility sharing the account sees only the transactions that are its own.</summary>
    public async Task<GatewaySettlementDetail?> GetAsync(Guid facilityId, Guid id, CancellationToken ct)
    {
        var settlement = await db.Set<GatewaySettlement>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == id && (s.FacilityId == facilityId || s.Lines.Any(l => l.FacilityId == facilityId)), ct);
        if (settlement is null)
        {
            return null;
        }

        var lines = settlement.Lines
            .Where(l => settlement.FacilityId == facilityId || l.FacilityId == facilityId)
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
            ToDto(settlement, settlement.Lines.Count, settlement.Lines.Count(l => l.Match != SettlementMatch.Matched)),
            lines.Select(l => new SettlementLineDto(
                    l.Id, l.Type, l.EntityId, l.PaymentId, l.OrderId, l.Method, l.TransactionAt, l.Amount, l.Fee, l.Tax, l.Net, l.Match, l.RecordedAmount,
                    l.ReceiptId, l.ReceiptId is { } receipt ? receipts.GetValueOrDefault(receipt) : null,
                    l.RefundId, l.RefundId is { } refund ? refunds.GetValueOrDefault(refund) : null))
                .ToList());
    }

    internal static GatewaySettlementDto ToDto(GatewaySettlement s, int lines, int unmatched) => new(
        s.Id, s.Provider, s.SettlementId, s.Utr, s.Status, s.SettledAt, s.SettledOn, s.PaymentsAmount, s.RefundsAmount, s.AdjustmentsAmount,
        s.Fees - s.Tax, s.Tax, s.Amount, s.Difference, lines, unmatched);

    /// <summary>Opens an exception for every transaction that does not match, and clears the ones a match settles. Saves.</summary>
    internal async Task<int> RaiseExceptionsAsync(IReadOnlyCollection<GatewaySettlement> settlements, CancellationToken ct)
    {
        if (settlements.Count == 0)
        {
            return 0;
        }

        var lines = settlements.SelectMany(s => s.Lines).ToList();
        var targets = await FindTargetsAsync(
            lines.Select(l => l.Type == PaymentTransactionType.Payment ? l.EntityId : l.PaymentId).OfType<string>().Distinct().ToList(),
            lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!).Distinct().ToList(),
            ct);
        var opened = 0;
        foreach (var settlement in settlements)
        {
            foreach (var line in settlement.Lines)
            {
                opened += await RaiseForLineAsync(settlement, line, targets, ct) ? 1 : 0;
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
                opened += db.Entry(exception).State == EntityState.Added ? 1 : 0;
            }
        }

        await SaveExceptionsAsync(ct);
        return opened;
    }

    private async Task<bool> RaiseForLineAsync(GatewaySettlement settlement, GatewaySettlementLine line, SettlementTargets targets, CancellationToken ct)
    {
        var target = targets.For(line);
        GatewayDiscrepancy? exception = null;
        switch (line)
        {
            case { Type: PaymentTransactionType.Payment, Match: SettlementMatch.Matched }:
                await discrepancies.ClearAsync(DiscrepancyKind.UnsettledReceipt, line.EntityId, $"Settled in {settlement.SettlementId} on {settlement.SettledOn:dd MMM yyyy}.", ct);
                await discrepancies.ClearAsync(DiscrepancyKind.UnrecordedPayment, line.EntityId, "The receipt was recorded.", ct);
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
                await discrepancies.ClearAsync(DiscrepancyKind.RefundMismatch, line.EntityId, "The gateway's refund matches HC360's.", ct);
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

        return exception is not null && db.Entry(exception).State == EntityState.Added;
    }

    /// <summary>Exceptions are saved on their own: a clash with a concurrent run must not undo the settlement already stored.</summary>
    private async Task SaveExceptionsAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            logger.LogInformation(ex, "Gateway exceptions were updated by a concurrent run; the next run sees them");
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Settlement journals for days that may already be in Tally go back in its queue (each facility its own part).</summary>
    private async Task QueueJournalsAsync(IEnumerable<GatewaySettlement> processed, CancellationToken ct)
    {
        var days = processed
            .SelectMany(s => s.Lines.Select(l => l.FacilityId).Append(s.FacilityId).Distinct().Select(f => (FacilityId: f, s.SettledOn)))
            .Distinct()
            .ToList();
        foreach (var (facility, date) in days)
        {
            await tally.RepostIfPostedAsync(facility, date, ct);
        }
    }

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
