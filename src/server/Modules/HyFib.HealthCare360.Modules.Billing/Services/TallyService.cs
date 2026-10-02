using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Tally day book (INT-TALLY, business side): gathers one facility's day of bills, cancellations, receipts, refunds,
/// credit notes, claim disallowances, doctor-fee accruals and payouts; builds balanced vouchers with the ledger mapping
/// from the <c>billing.tally.ledgers</c> setting; and pushes them through <see cref="IAccountingExporter"/> when one is
/// configured. The same vouchers are available as an import file for manual loading when Tally is unreachable.
/// </summary>
public sealed class TallyService(HospitalDbContext db, ISettingsService settings, IServiceProvider services, IOutbox outbox, TimeProvider time)
{
    public const string LedgerSetting = "billing.tally.ledgers";

    /// <summary>When true (default) the previous day's day book is queued automatically every night.</summary>
    public const string DailyPostSetting = "billing.tally.dailyPost.enabled";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<TallyDayBook> LoadDayBookAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(date, date);

        var bills = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Include(b => b.Lines)
            .Where(b => b.FacilityId == facilityId && b.BillNumber != null
                        && ((b.FinalizedAt >= start && b.FinalizedAt < end) || (b.CancelledAt >= start && b.CancelledAt < end)))
            .ToListAsync(ct);
        var receipts = await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Include(r => r.Payments)
            .Where(r => r.FacilityId == facilityId && r.ReceivedAt >= start && r.ReceivedAt < end)
            .OrderBy(r => r.ReceivedAt)
            .ToListAsync(ct);
        var disallowances = await (
                from s in db.Set<ClaimSettlement>()
                join c in db.Set<InsuranceClaim>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on s.ClaimId equals c.Id
                where c.FacilityId == facilityId && s.RecordedAt >= start && s.RecordedAt < end && s.DeductionAmount > 0
                select new { c.ClaimNumber, c.PayerId, s.DeductionAmount, s.Id })
            .ToListAsync(ct);
        var refunds = await db.Set<Refund>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.FacilityId == facilityId && r.Status == RefundStatus.Paid && r.Mode != null && r.PaidAt >= start && r.PaidAt < end)
            .OrderBy(r => r.PaidAt)
            .ToListAsync(ct);
        var creditNotes = await db.Set<CreditNote>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(c => c.FacilityId == facilityId && c.IssuedAt >= start && c.IssuedAt < end)
            .OrderBy(c => c.IssuedAt)
            .ToListAsync(ct);
        var fees = await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.FacilityId == facilityId && a.AccruedOn == date)
            .GroupBy(a => a.PractitionerId)
            .Select(g => new { PractitionerId = g.Key, Amount = g.Sum(a => a.ShareAmount) })
            .ToListAsync(ct);
        var payouts = await db.Set<DoctorPayout>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(p => p.FacilityId == facilityId && p.Status == PayoutStatus.Paid && p.PaidAt >= start && p.PaidAt < end)
            .ToListAsync(ct);
        var gatewaySettlements = await GatewaySettlementService.ForDayBookAsync(db, facilityId, date, ct);

        var payerIds = bills.Where(b => b.PayerId is not null).Select(b => b.PayerId!.Value)
            .Concat(receipts.Where(r => r.PayerId is not null).Select(r => r.PayerId!.Value))
            .Concat(disallowances.Select(d => d.PayerId))
            .Distinct()
            .ToList();
        var payers = await db.Set<Payer>().Where(p => payerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var departmentIds = bills.SelectMany(b => b.Lines).Where(l => l.DepartmentId is not null).Select(l => l.DepartmentId!.Value).Distinct().ToList();
        var departments = await db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(d => departmentIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => new { d.Code, d.Name }, ct);
        var doctorIds = fees.Select(f => f.PractitionerId).Concat(payouts.Select(p => p.PractitionerId)).Distinct().ToList();
        var doctors = await db.Set<Practitioner>().Where(p => doctorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);

        var sales = new List<TallySale>();
        foreach (var bill in bills.OrderBy(b => b.FinalizedAt))
        {
            var lines = bill.Lines.Select(l =>
            {
                var department = l.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null;
                return new TallySaleLine(
                    department?.Code, department?.Name, l.GrossAmount - l.PackageCoveredAmount, l.TariffDiscountAmount + l.LineDiscountAmount + l.BillDiscountShare,
                    l.CgstAmount, l.SgstAmount);
            }).ToList();
            var sale = new TallySale(
                bill.BillNumber!, date, bill.PayerId is { } payerId ? payers.GetValueOrDefault(payerId) : null, bill.PatientAmount, bill.PayerAmount,
                bill.RoundOffAmount, lines, IsCancellation: false);
            if (bill.FinalizedAt >= start && bill.FinalizedAt < end)
            {
                sales.Add(sale);
            }

            if (bill.CancelledAt >= start && bill.CancelledAt < end)
            {
                sales.Add(sale with { IsCancellation = true });
            }
        }

        return new TallyDayBook(
            date,
            sales,
            receipts.Select(r => new TallyReceipt(
                r.ReceiptNumber, date, r.Purpose, r.PayerId is { } payerId ? payers.GetValueOrDefault(payerId) : null,
                r.Payments.Select(p => new TallyPayment(p.Mode, p.Amount)).ToList())).ToList(),
            disallowances.Select(d => new TallyDisallowance($"{d.ClaimNumber}/{d.Id.ToString("N")[..6]}", date, payers.GetValueOrDefault(d.PayerId), d.DeductionAmount)).ToList(),
            refunds.Select(r => new TallyRefund(r.RefundNumber, date, r.Purpose, r.Mode!.Value, r.Amount)).ToList(),
            creditNotes.Select(c => new TallyCreditNote(c.Number, date, c.Kind, c.Amount)).ToList(),
            fees.Where(f => f.Amount != 0).Select(f => new TallyDoctorFee(doctors.GetValueOrDefault(f.PractitionerId) ?? "Doctor", f.Amount)).ToList(),
            payouts.Select(p => new TallyPayout(
                p.PayoutNumber, date, doctors.GetValueOrDefault(p.PractitionerId) ?? "Doctor", p.GrossAmount, p.TdsAmount, p.DeductionAmount, p.NetAmount)).ToList(),
            gatewaySettlements);
    }

    public async Task<IReadOnlyList<AccountingVoucher>> BuildVouchersAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var day = await LoadDayBookAsync(facilityId, date, ct);
        var ledgers = await settings.GetAsync<TallyLedgerMap>(LedgerSetting, facilityId, ct) ?? new TallyLedgerMap();
        return TallyVoucherBuilder.Build(day, ledgers);
    }

    /// <summary>Import file for manual loading: Tally XML from the configured exporter, otherwise the vouchers as JSON.</summary>
    public async Task<(byte[] Content, string ContentType, string FileName)> ExportFileAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var vouchers = await BuildVouchersAsync(facilityId, date, ct);
        var exporter = services.GetService<IAccountingExporter>();
        var stamp = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return exporter is not null
            ? (Encoding.UTF8.GetBytes(exporter.BuildImportFile(vouchers)), "application/xml; charset=utf-8", $"tally-daybook-{stamp}.xml")
            : (JsonSerializer.SerializeToUtf8Bytes(vouchers, Json), "application/json", $"tally-daybook-{stamp}.json");
    }

    /// <summary>Queues (or re-queues after a correction) a day book for posting through the outbox.</summary>
    public async Task<Result> QueueAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var export = await db.Set<TallyExport>().FirstOrDefaultAsync(e => e.FacilityId == facilityId && e.Date == date, ct);
        if (export is null)
        {
            db.Add(new TallyExport { FacilityId = facilityId, Date = date, Status = TallyExportStatus.Queued, CreatedAt = time.GetUtcNow() });
        }
        else
        {
            export.Status = TallyExportStatus.Queued;
            export.Message = null;
        }

        outbox.Enqueue(TallyDayBookHandler.MessageType, new TallyPostMessage(facilityId, date), facilityId);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Posts a day book (outbox handler). Failures are recorded on the export row and rethrown so the outbox retries with
    /// back-off; vouchers carry stable numbers, so a retried post alters rather than duplicates them in Tally.
    /// </summary>
    internal async Task PostAsync(TallyPostMessage message, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var export = await db.Set<TallyExport>().FirstOrDefaultAsync(e => e.FacilityId == message.FacilityId && e.Date == message.Date, ct);
        if (export is null)
        {
            export = new TallyExport { FacilityId = message.FacilityId, Date = message.Date, Status = TallyExportStatus.Queued, CreatedAt = now };
            db.Add(export);
        }

        export.Attempts++;
        export.LastAttemptAt = now;
        var vouchers = await BuildVouchersAsync(message.FacilityId, message.Date, ct);
        export.VoucherCount = vouchers.Count;

        var exporter = services.GetService<IAccountingExporter>();
        if (exporter is null)
        {
            export.Status = TallyExportStatus.NotConfigured;
            export.Message = "No accounting exporter is configured; download the day book file and import it into Tally.";
            return;
        }

        if (vouchers.Count == 0)
        {
            export.Status = TallyExportStatus.Posted;
            export.PostedAt = now;
            export.Message = "No transactions on this day.";
            return;
        }

        AccountingExportResult result;
        try
        {
            result = await exporter.PushVouchersAsync(vouchers, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MarkFailed(export, ex.Message);
            throw;
        }

        export.CreatedCount = result.Created;
        export.AlteredCount = result.Altered;
        export.ErrorCount = result.Errors;
        if (!result.Success || result.Errors > 0)
        {
            MarkFailed(export, result.Message ?? $"Tally rejected {result.Errors} voucher(s).");
            throw new InvalidOperationException(export.Message);
        }

        export.Status = TallyExportStatus.Posted;
        export.PostedAt = time.GetUtcNow();
        export.Message = result.Message;
    }

    private static void MarkFailed(TallyExport export, string message)
    {
        export.Status = TallyExportStatus.Failed;
        export.Message = message.Length > 2000 ? message[..2000] : message;
    }
}
