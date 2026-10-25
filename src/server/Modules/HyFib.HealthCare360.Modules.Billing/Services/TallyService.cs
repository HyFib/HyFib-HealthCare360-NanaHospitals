using System.Globalization;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Tally day book (INT-TALLY, business side): gathers one facility's day of bills, cancellations, receipts, refunds,
/// credit notes, claim disallowances, doctor-fee accruals and payouts, and the pharmacy's and canteen's days
/// (<see cref="ICounterSalesSource"/>); builds balanced vouchers with the ledger mapping from the
/// <c>billing.tally.ledgers</c> setting; and pushes them through <see cref="IAccountingExporter"/>, tracking what Tally
/// answered for every voucher (<see cref="TallyVoucherRecord"/>). The same vouchers are available as an import file for
/// sites without a Tally connection, where an accountant marks the day imported after loading it.
/// </summary>
public sealed class TallyService(
    HospitalDbContext db,
    ISettingsService settings,
    IServiceProvider services,
    IOutbox outbox,
    TimeProvider time,
    ILogger<TallyService> logger)
{
    public const string LedgerSetting = "billing.tally.ledgers";

    /// <summary>When true (default) the previous day's day book is queued automatically every night.</summary>
    public const string DailyPostSetting = "billing.tally.dailyPost.enabled";

    /// <summary>Most days that one request may queue.</summary>
    public const int MaxDaysPerQueue = 31;

    /// <summary>Vouchers handed to the exporter at a time; the answers are recorded as the day book goes out.</summary>
    internal const int ChunkSize = 50;

    /// <summary>
    /// The outbox gives a delivery 30 seconds. An attempt starts no new chunk after this; the rest of a large day book goes
    /// in a follow-up message of the same run.
    /// </summary>
    internal static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Calls to Tally in one attempt stop by this time (the exporter sends nothing new after it and cuts a call short),
    /// which leaves the attempt time to record Tally's answers inside the outbox's 30 seconds.
    /// </summary>
    internal static readonly TimeSpan CallDeadline = TimeSpan.FromSeconds(24);

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
                select new { c.ClaimNumber, c.PayerId, s.ClaimId, s.DeductionAmount, s.Id })
            .ToListAsync(ct);
        var claimIds = disallowances.Select(d => d.ClaimId).Distinct().ToList();
        var settlementsByClaim = (await db.Set<ClaimSettlement>().AsNoTracking().Where(s => claimIds.Contains(s.ClaimId)).Select(s => new { s.ClaimId, s.Id }).ToListAsync(ct))
            .ToLookup(s => s.ClaimId, s => s.Id);
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
        var doctors = await db.Set<Practitioner>().Where(p => doctorIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => new { p.DisplayName, p.Code }, ct);
        var facilityCode = await db.Set<Facility>().Where(f => f.Id == facilityId).Select(f => f.Code).FirstOrDefaultAsync(ct);
        var legacy = await db.Set<TallyExport>()
            .AnyAsync(e => e.FacilityId == facilityId && e.Date == date && e.LegacyReferences, ct);

        var sales = new List<TallySale>();
        foreach (var bill in bills.OrderBy(b => b.FinalizedAt))
        {
            var lines = bill.Lines.Select(l =>
            {
                var department = l.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null;
                return new TallySaleLine(
                    department?.Code, department?.Name, l.GrossAmount - l.PackageCoveredAmount, l.TariffDiscountAmount + l.LineDiscountAmount + l.BillDiscountShare,
                    l.CgstAmount, l.SgstAmount, l.IgstAmount);
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
            disallowances.Select(d => new TallyDisallowance(
                TallyVoucherBuilder.ClaimDeductionReference(d.ClaimNumber, d.Id, settlementsByClaim[d.ClaimId]), date, payers.GetValueOrDefault(d.PayerId), d.DeductionAmount)).ToList(),
            refunds.Select(r => new TallyRefund(r.RefundNumber, date, r.Purpose, r.Mode!.Value, r.Amount)).ToList(),
            creditNotes.Select(c => new TallyCreditNote(c.Number, date, c.Kind, c.Amount)).ToList(),
            fees.Where(f => f.Amount != 0).Select(f => new TallyDoctorFee(
                doctors.GetValueOrDefault(f.PractitionerId)?.DisplayName ?? "Doctor",
                f.Amount,
                doctors.GetValueOrDefault(f.PractitionerId)?.Code,
                f.PractitionerId)).ToList(),
            payouts.Select(p => new TallyPayout(
                p.PayoutNumber, date, doctors.GetValueOrDefault(p.PractitionerId)?.DisplayName ?? "Doctor", p.GrossAmount, p.TdsAmount, p.DeductionAmount, p.NetAmount)).ToList(),
            gatewaySettlements)
        {
            FacilityCode = facilityCode,
            Counters = await CountersAsync(facilityId, date, ct),
            LegacyDoctorFeeNumbers = legacy,
            PosSettlements = await PosSettlementService.ForDayBookAsync(db, facilityId, date, facilityCode, ct),
        };
    }

    public async Task<TallyLedgerMap> LedgersAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<TallyLedgerMap>(LedgerSetting, facilityId, ct) ?? new TallyLedgerMap();

    public async Task<IReadOnlyList<AccountingVoucher>> BuildVouchersAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var day = await LoadDayBookAsync(facilityId, date, ct);
        return TallyVoucherBuilder.Build(day, await LedgersAsync(facilityId, ct));
    }

    /// <summary>
    /// Import file for manual loading: Tally XML from the configured exporter, otherwise the vouchers as JSON. The day
    /// book's fingerprint is recorded on the day, so "mark imported" can tell whether the file is still current. A day
    /// with no posting record yet gets one only where Tally is not set up (elsewhere the nightly post creates it).
    /// </summary>
    public async Task<Result<(byte[] Content, string ContentType, string FileName)>> ExportFileAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var vouchers = await BuildVouchersAsync(facilityId, date, ct);
        var exporter = services.GetService<IAccountingExporter>();
        var stamp = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        (byte[] Content, string ContentType, string FileName) file;
        try
        {
            file = exporter is not null
                ? (Encoding.UTF8.GetBytes(exporter.BuildImportFile(vouchers)), "application/xml; charset=utf-8", $"tally-daybook-{stamp}.xml")
                : (JsonSerializer.SerializeToUtf8Bytes(vouchers, Json), "application/json", $"tally-daybook-{stamp}.json");
        }
        catch (InvalidOperationException ex)
        {
            return Error.Rule("billing.tally_file", ex.Message);
        }

        var fingerprint = TallyTracking.Fingerprint(vouchers);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RecordFileAsync(exporter, facilityId, date, vouchers.Count, fingerprint, ct);
                break;
            }
            catch (DbUpdateException ex) when (attempt < 3 && BillingDb.IsConflict(ex))
            {
                // A post or check of the day saved first (or created the day's row): record the file on the row as it
                // is now.
                db.ChangeTracker.Clear();
            }
        }

        return file;
    }

    /// <summary>Records the downloaded file on the day, creating the row only where Tally is not set up.</summary>
    private async Task RecordFileAsync(
        IAccountingExporter? exporter, Guid facilityId, DateOnly date, int voucherCount, string fingerprint, CancellationToken ct)
    {
        var export = await db.Set<TallyExport>().FirstOrDefaultAsync(e => e.FacilityId == facilityId && e.Date == date, ct);
        if (export is null && await ConnectionAsync(exporter, facilityId, ct) is { State: AccountingConnectionState.NotConfigured } connection)
        {
            export = new TallyExport
            {
                FacilityId = facilityId,
                Date = date,
                Status = TallyExportStatus.NotConfigured,
                CreatedAt = time.GetUtcNow(),
                VoucherCount = voucherCount,
                Message = NotConfiguredMessage(connection),
            };
            db.Add(export);
        }

        if (export is not null)
        {
            export.FileDownloadedAt = time.GetUtcNow();
            export.FileFingerprint = fingerprint;
            export.FileVoucherCount = voucherCount;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Queues (or re-queues after a correction) the day books of <paramref name="from"/>..<paramref name="to"/> for posting.</summary>
    public async Task<Result> QueueAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await db.Set<TallyExport>().Where(e => e.FacilityId == facilityId && e.Date >= from && e.Date <= to).ToDictionaryAsync(e => e.Date, ct);
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (!rows.TryGetValue(date, out var export))
            {
                export = new TallyExport { FacilityId = facilityId, Date = date, CreatedAt = now };
                db.Add(export);
            }

            export.StartRun(Guid.CreateVersion7(), now);
            outbox.Enqueue(TallyDayBookHandler.MessageType, new TallyPostMessage(facilityId, date, export.RunId), facilityId);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            // Someone queued one of the days at the same moment (one posting record per facility and day), or a post or
            // check of one of them saved first.
            return Error.Conflict("billing.tally_busy", "Another post of these days started at the same time. Look at the posting log, then try again if needed.");
        }

        return Result.Success();
    }

    /// <summary>
    /// Posts a day book (outbox handler; it runs in a scope of its own and saves as it goes, so the outbox's transaction
    /// holds no lock on the day). Each attempt sends what Tally has not confirmed in the current run, records Tally's
    /// answer for every voucher, and checks that Tally confirmed every voucher of the day book. Failures are recorded and
    /// rethrown so the outbox retries with back-off; vouchers carry stable references, so a retry alters rather than
    /// duplicates them in Tally. A day with no Tally connection is marked as such and not retried. Calls to Tally stop at
    /// <see cref="CallDeadline"/>, so an attempt ends inside the outbox's time limit.
    /// </summary>
    internal async Task PostAsync(TallyPostMessage message, CancellationToken ct)
    {
        try
        {
            await PostAttemptAsync(message, ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            // A check or "mark imported" saved the day first. The retry sends again whatever is not recorded as confirmed;
            // Tally alters what it already holds, so nothing is doubled.
            throw new InvalidOperationException(
                "The day changed while it was being posted (a check ran at the same time). HC360 posts it again shortly.", ex);
        }
    }

    /// <summary>
    /// Records that a post attempt ran out of the outbox's time: the day shows Failed, with why, instead of staying
    /// Queued. Called by the outbox handler in a fresh scope after the attempt was cancelled.
    /// </summary>
    internal async Task RecordPostTimeoutAsync(TallyPostMessage message, TimeSpan limit, CancellationToken ct)
    {
        var export = await db.Set<TallyExport>()
            .FirstOrDefaultAsync(e => e.FacilityId == message.FacilityId && e.Date == message.Date, ct);
        if (export is null || export.Status != TallyExportStatus.Queued || (message.RunId is { } runId && export.RunId != runId))
        {
            return;
        }

        MarkFailed(export, string.Create(CultureInfo.InvariantCulture,
            $"The post did not finish within the {limit.TotalSeconds:0} seconds HC360 gives one attempt. ")
            + "HC360 tries again shortly; vouchers Tally already confirmed are not sent again.");
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            // The day moved on meanwhile (a new run, a check); its own status is the better record.
        }
    }

    private async Task PostAttemptAsync(TallyPostMessage message, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var now = time.GetUtcNow();
        var export = await db.Set<TallyExport>().FirstOrDefaultAsync(e => e.FacilityId == message.FacilityId && e.Date == message.Date, ct);
        if (export is null)
        {
            export = new TallyExport { FacilityId = message.FacilityId, Date = message.Date, CreatedAt = now };
            export.StartRun(message.RunId ?? Guid.CreateVersion7(), now);
            db.Add(export);
        }
        else if (message.RunId is { } runId && export.RunId != runId)
        {
            logger.LogInformation("Skipped a superseded Tally post of {Date} for facility {FacilityId}", message.Date, message.FacilityId);
            return;
        }
        else if (export.RunId is null)
        {
            // Queued before HC360 tracked post runs.
            export.StartRun(Guid.CreateVersion7(), export.QueuedAt ?? now);
        }

        export.Attempts++;
        export.LastAttemptAt = now;
        var vouchers = await BuildVouchersAsync(message.FacilityId, message.Date, ct);
        if (TallyTracking.KeyProblem(vouchers) is { } problem)
        {
            // A retry cannot fix this.
            MarkFailed(export, problem + " Nothing was sent. Report this to HC360 support.");
            logger.LogError(
                "The Tally day book of {Date} for facility {FacilityId} cannot be tracked: {Problem}", message.Date, message.FacilityId, problem);
            await db.SaveChangesAsync(ct);
            return;
        }

        var billDaysNote = BillDaysNote(
            export.LegacyReferences && export.Attempts == 1 ? await RequeueBillDaysAsync(export, ct) : []);
        var records = await db.Set<TallyVoucherRecord>().Where(r => r.ExportId == export.Id).ToListAsync(ct);
        var tracked = Track(export, records, vouchers);
        var all = records.Except(tracked.Removed).Concat(tracked.Added).ToList();
        if (vouchers.Count == 0)
        {
            Finish(export, all, "No transactions on this day; nothing to send." + billDaysNote);
            await db.SaveChangesAsync(ct);
            return;
        }

        var exporter = services.GetService<IAccountingExporter>();
        var connection = await ConnectionAsync(exporter, message.FacilityId, ct);
        if (connection.State == AccountingConnectionState.NotConfigured)
        {
            TallyTracking.Summarize(export, all);
            export.Status = TallyExportStatus.NotConfigured;
            export.Message = NotConfiguredMessage(connection) + billDaysNote;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (connection.State == AccountingConnectionState.Invalid)
        {
            TallyTracking.Summarize(export, all);
            throw await FailedAsync(export, (connection.Message ?? "The Tally settings cannot be used.") + billDaysNote, ct);
        }

        var pending = vouchers.Where(v => TallyTracking.NeedsSending(tracked.Current[v.Key], export.RunId)).ToList();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remaining = CallDeadline - time.GetElapsedTime(started);
        deadline.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        var timedOut = string.Create(CultureInfo.InvariantCulture,
                $"Tally did not answer within the {CallDeadline.TotalSeconds:0} seconds HC360 gives one attempt. ")
            + "HC360 tries again shortly; vouchers Tally already confirmed are not sent again.";
        var sent = 0;
        foreach (var chunk in pending.Chunk(ChunkSize))
        {
            if (sent > 0 && time.GetElapsedTime(started) > AttemptBudget)
            {
                // A large day book continues in a new message of the same run, so no attempt runs into the outbox time limit.
                TallyTracking.Summarize(export, all);
                export.Message = string.Create(CultureInfo.InvariantCulture,
                    $"Tally has confirmed {export.ConfirmedCount} of {export.VoucherCount} vouchers so far; the rest follow in a moment.");
                outbox.Enqueue(TallyDayBookHandler.MessageType, new TallyPostMessage(message.FacilityId, message.Date, export.RunId), message.FacilityId);
                await db.SaveChangesAsync(ct);
                return;
            }

            AccountingExportResult result;
            try
            {
                result = await exporter!.PushVouchersAsync(chunk, message.FacilityId, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                TallyTracking.Summarize(export, all);
                throw await FailedAsync(export, timedOut + billDaysNote, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TallyTracking.Summarize(export, all);
                MarkFailed(export, ex.Message + billDaysNote);
                await db.SaveChangesAsync(ct);
                throw;
            }

            TallyTracking.Apply(tracked.Current, chunk, result, export.RunId, time.GetUtcNow());
            sent += chunk.Length;
            if (result.NotConfigured)
            {
                TallyTracking.Summarize(export, all);
                export.Status = TallyExportStatus.NotConfigured;
                export.Message = NotConfiguredMessage(
                    new AccountingConnection(AccountingConnectionState.NotConfigured, null, result.Message)) + billDaysNote;
                await db.SaveChangesAsync(ct);
                return;
            }

            if (result.Vouchers.Any(a => a.Outcome == AccountingVoucherOutcome.NotSent))
            {
                // Tally went away, or the attempt ran out of time: what is left would fail the same way. The outbox tries again later.
                TallyTracking.Summarize(export, all);
                var reason = deadline.IsCancellationRequested ? timedOut : result.Message ?? "Tally could not be reached.";
                throw await FailedAsync(export, reason + billDaysNote, ct);
            }

            // Keep Tally's answers as they come, so a later failure does not lose them.
            TallyTracking.Summarize(export, all);
            await db.SaveChangesAsync(ct);
        }

        TallyTracking.Summarize(export, all);
        var refused = tracked.Current.Values.Where(r => r.Answer is TallyVoucherAnswer.Rejected or TallyVoucherAnswer.Unconfirmed).ToList();
        if (refused.Count > 0 || export.ConfirmedCount != export.VoucherCount)
        {
            var detail = string.Join("; ", refused.Take(3).Select(r => $"{r.Number}: {r.AnswerMessage ?? "not confirmed"}"));
            var confirmed = string.Create(CultureInfo.InvariantCulture,
                $"Tally confirmed {export.ConfirmedCount} of {export.VoucherCount} vouchers.");
            throw await FailedAsync(export, confirmed + (detail.Length > 0 ? " " + detail : string.Empty) + billDaysNote, ct);
        }

        Finish(export, all, string.Create(CultureInfo.InvariantCulture, $"Tally confirmed all {export.VoucherCount} vouchers.")
                            + billDaysNote);
        outbox.Enqueue(TallyCheckHandler.MessageType, new TallyCheckMessage(message.FacilityId, message.Date), message.FacilityId, delay: TimeSpan.FromSeconds(10));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records the failure on the day; the caller throws what it returns, so the outbox retries with back-off.</summary>
    private async Task<InvalidOperationException> FailedAsync(TallyExport export, string message, CancellationToken ct)
    {
        MarkFailed(export, message);
        await db.SaveChangesAsync(ct);
        return new InvalidOperationException(export.Message);
    }

    /// <summary>
    /// For a day posted before HC360 tracked each voucher: that post sent a bill cancelled on the day under the bill's own
    /// reference, so Tally turned the bill's sale (HC360-&lt;bill&gt;) into a credit note. The day book now reverses the
    /// bill with a credit note of its own (&lt;bill&gt;/C), so the bill's own day is queued again: posting it turns
    /// Tally's voucher back into the sale, and Tally does not reverse the bill twice. A day that has sent the bill since
    /// per-voucher tracking began, or that is queued already, is left alone. Returns the days queued.
    /// </summary>
    private async Task<IReadOnlyList<DateOnly>> RequeueBillDaysAsync(TallyExport export, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(export.Date, export.Date);
        var bills = await db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.FacilityId == export.FacilityId && b.BillNumber != null
                        && b.CancelledAt >= start && b.CancelledAt < end && b.FinalizedAt < start)
            .Select(b => new { Number = b.BillNumber!, FinalizedAt = b.FinalizedAt!.Value })
            .ToListAsync(ct);
        if (bills.Count == 0)
        {
            return [];
        }

        var byDay = bills.GroupBy(b => BillingTime.LocalDate(b.FinalizedAt))
            .ToDictionary(g => g.Key, g => g.Select(b => b.Number).ToList());
        var dates = byDay.Keys.ToList();
        var days = await db.Set<TallyExport>()
            .Where(e => e.FacilityId == export.FacilityId && dates.Contains(e.Date))
            .ToDictionaryAsync(e => e.Date, ct);
        var dayIds = days.Values.Select(e => e.Id).ToList();
        var numbers = bills.Select(b => b.Number).ToList();
        var sentSince = (await db.Set<TallyVoucherRecord>().AsNoTracking()
                .Where(r => dayIds.Contains(r.ExportId) && numbers.Contains(r.Key) && r.SentHash != null)
                .Select(r => new { r.ExportId, r.Key })
                .ToListAsync(ct))
            .Select(r => (r.ExportId, r.Key))
            .ToHashSet();

        var queued = new List<DateOnly>();
        var now = time.GetUtcNow();
        foreach (var (date, billNumbers) in byDay.OrderBy(d => d.Key))
        {
            var day = days.GetValueOrDefault(date);
            var sentAlready = day is not null && billNumbers.All(n => sentSince.Contains((day.Id, n)));
            if (day is { Status: TallyExportStatus.Queued } || sentAlready)
            {
                continue;
            }

            if (day is null)
            {
                day = new TallyExport { FacilityId = export.FacilityId, Date = date, CreatedAt = now };
                db.Add(day);
            }

            day.StartRun(Guid.CreateVersion7(), now);
            outbox.Enqueue(TallyDayBookHandler.MessageType, new TallyPostMessage(export.FacilityId, date, day.RunId), export.FacilityId);
            queued.Add(date);
            logger.LogInformation(
                "Queued the Tally day book of {BillDay} again for bills cancelled on {Date}, posted before per-voucher tracking",
                date, export.Date);
        }

        return queued;
    }

    private static string BillDaysNote(IReadOnlyList<DateOnly> days) =>
        days.Count == 0
            ? string.Empty
            : " Bills cancelled on this day were sent to Tally before HC360 tracked each voucher, so their own days are posted "
              + "again to turn them back into sales: "
              + string.Join(", ", days.Select(d => d.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture))) + ".";

    /// <summary>
    /// Records that the downloaded file was imported into Tally by hand: every voucher of the day book is marked imported
    /// and the day's status becomes Imported. Refused when no file was downloaded, or when the day book changed since, so
    /// the record always describes the file that was loaded; refused too while the day is being posted, or once Tally
    /// confirmed the day itself (the screen hides the action then).
    /// </summary>
    public async Task<Result<TallyExport>> MarkImportedAsync(Guid facilityId, DateOnly date, Guid? userId, string? userName, CancellationToken ct)
    {
        try
        {
            return await MarkImportedOnceAsync(facilityId, date, userId, userName, ct);
        }
        catch (DbUpdateException ex) when (BillingDb.IsConflict(ex))
        {
            return Error.Conflict("billing.tally_busy",
                "The day changed while it was being marked imported (a post or a check ran at the same time). "
                + "Look at the day again, then mark it imported if it still needs it.");
        }
    }

    private async Task<Result<TallyExport>> MarkImportedOnceAsync(
        Guid facilityId, DateOnly date, Guid? userId, string? userName, CancellationToken ct)
    {
        var export = await db.Set<TallyExport>().FirstOrDefaultAsync(e => e.FacilityId == facilityId && e.Date == date, ct);
        if (export?.FileFingerprint is null)
        {
            return Error.Rule("billing.tally_no_file", "Download the day book file first, import it into Tally, then mark the day imported.");
        }

        if (export.Status == TallyExportStatus.Queued)
        {
            return Error.Conflict("billing.tally_posting",
                "The day is being posted to Tally now. Wait for the post to finish; "
                + "mark the day imported only if it does not reach Tally.");
        }

        if (export.Status == TallyExportStatus.Posted)
        {
            return Error.Rule("billing.tally_posted",
                "Tally confirmed this day itself, so there is nothing to mark imported. Post the day again if it changed.");
        }

        var vouchers = await BuildVouchersAsync(facilityId, date, ct);
        if (TallyTracking.Fingerprint(vouchers) != export.FileFingerprint)
        {
            var downloaded = BillingTime.ToLocal(export.FileDownloadedAt ?? time.GetUtcNow()).ToString("dd-MMM-yyyy HH:mm", CultureInfo.InvariantCulture);
            return Error.Rule("billing.tally_file_outdated",
                $"The day book changed after the file was downloaded ({downloaded}). Download the file again, import it into Tally, then mark the day imported.");
        }

        if (TallyTracking.KeyProblem(vouchers) is { } problem)
        {
            return Error.Rule("billing.tally_duplicate", problem + " Report this to HC360 support.");
        }

        var now = time.GetUtcNow();
        var records = await db.Set<TallyVoucherRecord>().Where(r => r.ExportId == export.Id).ToListAsync(ct);
        var tracked = Track(export, records, vouchers);
        TallyTracking.MarkImported(tracked.Current.Values, now);

        // The accountant deleted in Tally the vouchers that left the day book (the screen lists them before marking).
        var withdrawn = records.Except(tracked.Removed).Where(r => !r.InDayBook && r.Match != TallyMatch.OnlyInTally).ToList();
        db.RemoveRange(withdrawn);
        var all = records.Except(tracked.Removed).Except(withdrawn).Concat(tracked.Added).ToList();

        export.Status = TallyExportStatus.Imported;
        export.ImportedAt = now;
        export.ImportedByUserId = userId;
        export.ImportedByName = TallyTracking.Bound(userName, 120);
        export.Message = $"Imported into Tally from the file{(userName is null ? string.Empty : " by " + TallyTracking.Bound(userName, 120))}.";
        TallyTracking.Summarize(export, all);
        await db.SaveChangesAsync(ct);
        return export;
    }

    /// <summary>The day book's vouchers with what Tally answered for each, then those that left it and those only in Tally.</summary>
    public async Task<(TallyExport? Export, IReadOnlyList<TallyVoucherRecord> Records)> VoucherRecordsAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var export = await db.Set<TallyExport>().AsNoTracking().FirstOrDefaultAsync(e => e.FacilityId == facilityId && e.Date == date, ct);
        var records = export is null ? [] : await db.Set<TallyVoucherRecord>().AsNoTracking().Where(r => r.ExportId == export.Id).ToListAsync(ct);
        var vouchers = await BuildVouchersAsync(facilityId, date, ct);

        // Read-only: matching the day book to the records here is never saved.
        var tracked = TallyTracking.Track(export ?? new TallyExport { FacilityId = facilityId, Date = date }, records, vouchers);
        var current = vouchers.Select(v => v.Key).Distinct(StringComparer.Ordinal).Select(key => tracked.Current[key]).ToList();
        var rest = records.Except(tracked.Removed).Where(r => !tracked.Current.ContainsKey(r.Key)).OrderBy(r => r.Match).ThenBy(r => r.Key, StringComparer.Ordinal);
        return (export, [.. current, .. rest]);
    }

    internal static string NotConfiguredMessage(AccountingConnection connection) =>
        (connection.Message ?? "Tally is not set up for this facility.") + " Download the day book file, import it into Tally, then mark the day imported.";

    internal static async Task<AccountingConnection> ConnectionAsync(IAccountingExporter? exporter, Guid facilityId, CancellationToken ct) =>
        exporter is null
            ? new AccountingConnection(AccountingConnectionState.NotConfigured, null, "No Tally connection is installed on this server.")
            : await exporter.GetConnectionAsync(facilityId, ct);

    private TallyTracked Track(TallyExport export, IReadOnlyList<TallyVoucherRecord> records, IReadOnlyList<AccountingVoucher> vouchers)
    {
        var tracked = TallyTracking.Track(export, records, vouchers);
        db.AddRange(tracked.Added);
        db.RemoveRange(tracked.Removed);
        return tracked;
    }

    private async Task<IReadOnlyList<CounterSalesDay>> CountersAsync(Guid facilityId, DateOnly date, CancellationToken ct)
    {
        var days = new List<CounterSalesDay>();
        foreach (var source in services.GetServices<ICounterSalesSource>())
        {
            days.Add(await source.GetDayAsync(facilityId, date, ct));
        }

        return days;
    }

    private void Finish(TallyExport export, IReadOnlyList<TallyVoucherRecord> all, string message)
    {
        TallyTracking.Summarize(export, all);
        var withdrawn = all.Where(r => !r.InDayBook && r.Match != TallyMatch.OnlyInTally).Select(r => r.Number).ToList();
        export.Status = TallyExportStatus.Posted;
        export.PostedAt = time.GetUtcNow();
        export.Message = withdrawn.Count == 0
            ? message
            : message + string.Create(CultureInfo.InvariantCulture,
                $" {withdrawn.Count} voucher(s) sent before are no longer in the day book; delete them in Tally: {string.Join(", ", withdrawn.Take(5))}.");
    }

    private static void MarkFailed(TallyExport export, string message)
    {
        export.Status = TallyExportStatus.Failed;
        export.Message = message.Length > 2000 ? message[..2000] : message;
    }
}
