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
    /// <summary>
    /// Reading a day back from Tally stops after this, inside the outbox's 30 seconds for the whole check.
    /// </summary>
    internal static readonly TimeSpan ReadBackDeadline = TimeSpan.FromSeconds(22);

    private const int SaveAttempts = 3;

    /// <summary>The check as it stands now, without keeping anything.</summary>
    public Task<TallyDayCheckDto> PreviewAsync(Guid facilityId, DateOnly date, CancellationToken ct) => RunAsync(facilityId, date, store: false, againstTally: false, ct);

    /// <summary>
    /// Runs the check, against Tally too when asked, and keeps the results on the day. A post (or another check) that
    /// saves the day first makes the check start again from what was saved, so neither overwrites the other.
    /// </summary>
    public async Task<TallyDayCheckDto> CheckAsync(
        Guid facilityId, DateOnly date, bool againstTally, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunAsync(facilityId, date, store: true, againstTally, ct);
            }
            catch (DbUpdateException ex) when (attempt < SaveAttempts && BillingDb.IsConflict(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// Records that a check ran out of the outbox's time: the day's check against Tally shows Failed, with why. Called
    /// by the outbox handler in a fresh scope; the check is not retried, staff run it again from the Tally screen.
    /// </summary>
    internal async Task RecordTimeoutAsync(Guid facilityId, DateOnly date, TimeSpan limit, CancellationToken ct)
    {
        var export = await db.Set<TallyExport>()
            .FirstOrDefaultAsync(e => e.FacilityId == facilityId && e.Date == date, ct);
        if (export is null)
        {
            return;
        }

        export.TallyCheck = TallyCheckStatus.Failed;
        export.TallyCheckedAt = time.GetUtcNow();
        export.TallyCheckMessage = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"The check did not finish within the {limit.TotalSeconds:0} seconds HC360 gives it. ")
            + "Run it again from the Tally screen.";
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            // Someone checked or posted the day meanwhile; their result stands.
        }
    }

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
        var keyProblem = TallyTracking.KeyProblem(vouchers);
        var tracked = keyProblem is not null
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
            notes.Insert(0, keyProblem + " Report this to HC360 support.");
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

        AccountingReadBack readBack;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(ReadBackDeadline);
            try
            {
                readBack = await exporter.ReadBackAsync(export.Date, vouchers, export.FacilityId, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                export.TallyCheck = TallyCheckStatus.Failed;
                export.TallyCheckMessage = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"Tally did not send the day's vouchers within {ReadBackDeadline.TotalSeconds:0} seconds. ")
                    + "Run the check again when Tally is less busy.";
                return records;
            }
        }

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
        var (added, removed) = TallyTracking.ApplyCheck(
            export, records, readBack, elsewhere.ToHashSet(StringComparer.OrdinalIgnoreCase), now,
            await BillDaysAsync(export, otherKeys, ct));
        db.AddRange(added);
        db.RemoveRange(removed);
        return records.Except(removed).Concat(added).ToList();
    }

    /// <summary>
    /// The facility's bills among HC360 vouchers only Tally holds on the day, with the day each was finalised. Such a
    /// voucher is a bill a pre-tracking post turned into a credit note: posting the bill's day restores it, deleting it
    /// would lose the sale.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, DateOnly>> BillDaysAsync(
        TallyExport export, IReadOnlyList<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return new Dictionary<string, DateOnly>();
        }

        var bills = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.FacilityId == export.FacilityId && b.BillNumber != null && keys.Contains(b.BillNumber)
                        && b.FinalizedAt != null)
            .Select(b => new { Number = b.BillNumber!, FinalizedAt = b.FinalizedAt!.Value })
            .ToListAsync(ct);
        var days = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
        foreach (var bill in bills)
        {
            days[bill.Number] = BillingTime.LocalDate(bill.FinalizedAt);
        }

        return days;
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
