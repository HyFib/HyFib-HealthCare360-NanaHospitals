using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tally;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Reconciles a facility's day book (INT-TALLY):
/// <list type="number">
/// <item>Inside HC360: the day book's figures against the records it is built from — bills, money received and paid
/// back per payment mode against the cashier day-end, deposits and advances, settlements, credit notes, write-offs,
/// claim deductions and doctor fees (<see cref="TallyRecordsCheck"/>) — and every voucher of the day book against what
/// Tally confirmed (<see cref="TallyTracking"/>).</item>
/// <item>Against TallyPrime, where it is reachable: the day's vouchers read back from Tally, matched by remote id
/// (present, amount, type, ledgers), and HC360 vouchers only Tally still holds.</item>
/// </list>
/// Results are kept on the day (<see cref="TallyExport"/>) and its vouchers, so the posting log shows them.
/// </summary>
public sealed class TallyReconciliationService(HospitalDbContext db, TallyService tally, ShiftService shifts, IServiceProvider services, TimeProvider time)
{
    /// <summary>The check as it stands now, without keeping anything.</summary>
    public Task<TallyDayCheckDto> PreviewAsync(Guid facilityId, DateOnly date, CancellationToken ct) => RunAsync(facilityId, date, store: false, againstTally: false, ct);

    /// <summary>Runs the check, against Tally too when asked, and keeps the results on the day.</summary>
    public Task<TallyDayCheckDto> CheckAsync(Guid facilityId, DateOnly date, bool againstTally, CancellationToken ct) => RunAsync(facilityId, date, store: true, againstTally, ct);

    private async Task<TallyDayCheckDto> RunAsync(Guid facilityId, DateOnly date, bool store, bool againstTally, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var day = await tally.LoadDayBookAsync(facilityId, date, ct);
        var map = await tally.LedgersAsync(facilityId, ct);
        var vouchers = TallyVoucherBuilder.Build(day, map);
        var comparison = TallyRecordsCheck.Compare(await FiguresAsync(facilityId, date, ct), day, map);

        var exports = db.Set<TallyExport>().Where(e => e.FacilityId == facilityId && e.Date == date);
        var export = await (store ? exports : exports.AsNoTracking()).FirstOrDefaultAsync(ct);
        var stored = export is null ? [] : await (store ? db.Set<TallyVoucherRecord>() : db.Set<TallyVoucherRecord>().AsNoTracking()).Where(r => r.ExportId == export.Id).ToListAsync(ct);
        var tracked = TallyTracking.DuplicateKeys(vouchers).Count > 0
            ? null
            : TallyTracking.Track(export ?? new TallyExport { FacilityId = facilityId, Date = date }, stored, vouchers);
        var records = tracked is null ? stored : stored.Except(tracked.Removed).Concat(tracked.Added).ToList();
        var keep = store && export is not null && tracked is not null;
        if (keep)
        {
            db.AddRange(tracked!.Added);
            db.RemoveRange(tracked.Removed);
        }

        var exporter = services.GetService<IAccountingExporter>();
        var connection = await TallyService.ConnectionAsync(exporter, facilityId, ct);
        if (againstTally && keep)
        {
            records = await CheckTallyAsync(export!, records, vouchers, exporter!, connection, now, ct);
        }

        var sending = TallyTracking.Count(records);
        var differences = comparison.Differences + sending.NotSent + sending.Refused + sending.ChangedSinceSent + sending.NoLongerInHc360;
        if (tracked is null)
        {
            differences++;
        }

        var status = differences == 0 ? TallyCheckStatus.Matched : TallyCheckStatus.Differences;
        if (keep)
        {
            export!.RecordsCheck = status;
            export.RecordsCheckedAt = now;
            export.RecordsDifferences = differences;
            TallyTracking.Summarize(export, records);
            await db.SaveChangesAsync(ct);
        }

        var notes = comparison.Notes.ToList();
        if (tracked is null)
        {
            notes.Insert(0, "Some vouchers share a reference, so Tally would keep only one of them. Report this to HC360 support.");
        }

        if (export is null && vouchers.Count > 0)
        {
            notes.Insert(0, "This day has not been posted to Tally yet.");
        }

        return new TallyDayCheckDto(
            date,
            export?.Status,
            status,
            comparison.Lines,
            notes,
            sending,
            export?.TallyCheck ?? TallyCheckStatus.NotChecked,
            export?.TallyCheckedAt,
            export?.TallyDifferences ?? 0,
            export?.TallyCheckMessage,
            connection.State == AccountingConnectionState.Ready && export is not null);
    }

    /// <summary>Reads the day back from Tally and keeps the verdict on each voucher; a day Tally cannot answer for keeps its records.</summary>
    private async Task<List<TallyVoucherRecord>> CheckTallyAsync(
        TallyExport export, List<TallyVoucherRecord> records, IReadOnlyList<AccountingVoucher> vouchers, IAccountingExporter exporter,
        AccountingConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        export.TallyCheckedAt = now;
        if (connection.State != AccountingConnectionState.Ready)
        {
            export.TallyCheck = TallyCheckStatus.NotChecked;
            export.TallyCheckMessage = TallyTracking.Bound(connection.State == AccountingConnectionState.NotConfigured
                ? "Tally is not set up for this facility, so its vouchers cannot be read back. Mark the day imported once the file is loaded."
                : connection.Message, TallyTracking.MaxText);
            return records;
        }

        var readBack = await exporter.ReadBackAsync(export.Date, vouchers, export.FacilityId, ct);
        if (!readBack.Success)
        {
            export.TallyCheck = readBack.NotConfigured ? TallyCheckStatus.NotChecked : TallyCheckStatus.Failed;
            export.TallyCheckMessage = TallyTracking.Bound(readBack.Message ?? "Tally could not be read.", TallyTracking.MaxText);
            return records;
        }

        // An HC360 voucher of another facility posted to the same Tally company is that facility's, not left over.
        var otherKeys = readBack.Others.Select(o => o.Key).ToList();
        var elsewhere = otherKeys.Count == 0
            ? []
            : await db.Set<TallyVoucherRecord>().AsNoTracking()
                .Where(r => r.Date == export.Date && r.FacilityId != export.FacilityId && r.Match != TallyMatch.OnlyInTally && otherKeys.Contains(r.Key))
                .Select(r => r.Key)
                .ToListAsync(ct);
        var (added, removed) = TallyTracking.ApplyCheck(export, records, readBack, elsewhere.ToHashSet(StringComparer.OrdinalIgnoreCase), now);
        db.AddRange(added);
        db.RemoveRange(removed);
        return records.Except(removed).Concat(added).ToList();
    }

    /// <summary>The day's figures read straight from the records, with the facility named (background work has no facility filter).</summary>
    private async Task<TallyRecordFigures> FiguresAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(date, date);
        var bills = db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(b => b.FacilityId == facilityId && b.BillNumber != null);
        var finalised = await bills.Where(b => b.FinalizedAt >= start && b.FinalizedAt < end).Select(b => b.NetAmount).ToListAsync(ct);
        var cancelled = await bills.Where(b => b.CancelledAt >= start && b.CancelledAt < end).Select(b => b.NetAmount).ToListAsync(ct);
        var payments = await db.Set<Receipt>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.FacilityId == facilityId && r.ReceivedAt >= start && r.ReceivedAt < end)
            .SelectMany(r => r.Payments, (r, p) => new { r.Purpose, p.Mode, p.Amount })
            .ToListAsync(ct);
        var credits = await db.Set<CreditNote>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(c => c.FacilityId == facilityId && c.IssuedAt >= start && c.IssuedAt < end)
            .Select(c => new { c.Kind, c.Amount })
            .ToListAsync(ct);
        var deductions = await (
                from s in db.Set<ClaimSettlement>()
                join c in db.Set<InsuranceClaim>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on s.ClaimId equals c.Id
                where c.FacilityId == facilityId && s.RecordedAt >= start && s.RecordedAt < end
                select s.DeductionAmount)
            .ToListAsync(ct);
        var accrued = await db.Set<DoctorFeeAccrual>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.FacilityId == facilityId && a.AccruedOn == date)
            .Select(a => a.ShareAmount)
            .ToListAsync(ct);
        var paidOut = await db.Set<DoctorPayout>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(p => p.FacilityId == facilityId && p.Status == PayoutStatus.Paid && p.PaidAt >= start && p.PaidAt < end)
            .Select(p => p.NetAmount)
            .ToListAsync(ct);
        var dayEnd = await shifts.DayEndAsync(facilityId, date, null, ct);

        return new TallyRecordFigures
        {
            BillsFinalised = finalised.Count,
            BillsFinalisedAmount = finalised.Sum(),
            BillsCancelled = cancelled.Count,
            BillsCancelledAmount = cancelled.Sum(),
            DayEnd = dayEnd.ByMode.Select(m => new ShiftModeTotal(m.Mode, m.Collected, m.Refunded)).ToList(),
            AdvancesCollected = dayEnd.DepositsCollected,
            AdvancesAdjusted = payments.Where(p => p.Mode == PaymentMode.Advance).Sum(p => p.Amount),
            PayerSettlements = payments.Where(p => p.Purpose == ReceiptPurpose.PayerSettlement).Sum(p => p.Amount),
            CreditNotes = credits.Count(c => c.Kind == CreditNoteKind.CreditNote),
            CreditNotesAmount = credits.Where(c => c.Kind == CreditNoteKind.CreditNote).Sum(c => c.Amount),
            WriteOffs = credits.Count(c => c.Kind == CreditNoteKind.WriteOff),
            WriteOffsAmount = credits.Where(c => c.Kind == CreditNoteKind.WriteOff).Sum(c => c.Amount),
            ClaimDeductions = deductions.Where(d => d > 0).Sum(),
            DoctorFeesAccrued = accrued.Sum(),
            Payouts = paidOut.Count,
            PayoutsNet = paidOut.Sum(),
            OpenShifts = dayEnd.OpenShifts,
            CashVariances = dayEnd.Shifts.Where(s => s.Variance is not (null or 0)).Select(s => new TallyCashVariance(s.Counter, s.UserName, s.Variance!.Value)).ToList(),
        };
    }
}
