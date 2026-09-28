using System.Globalization;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>Today's QC position for a set of analytes.</summary>
internal sealed record QcDayState(IReadOnlyList<QcLockout> Lockouts, IReadOnlySet<Guid> AcceptedTestIds);

internal sealed record QcRunFact(Guid Id, Guid LabTestId, Guid QcLotId, DateTimeOffset RunAt, QcOutcome Outcome, List<string> Violations);

/// <summary>
/// Internal quality control (LIS-06). Each run is judged by the Westgard rules against the assay's recent accepted
/// runs. The assay's state today drives two safety rules. First, verification is locked while the latest run of the
/// day on any control lot is rejected, unless a supervisor records an audited override. Second, auto-validation is
/// allowed only when every lot run today was accepted. Tests in one QC group (fasting and post-prandial glucose) share
/// the assay's QC.
/// </summary>
internal sealed class QualityControlService(
    HospitalDbContext db,
    DiagnosticsLookups lookups,
    DiagnosticsRealtime realtime,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<QualityControlService> logger)
{
    public async Task<Result<QcRunDto>> RecordRunAsync(RecordQcRunRequest request, CancellationToken ct)
    {
        var lot = await db.Set<QcLot>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.LotId, ct);
        if (lot is null)
        {
            return Error.NotFound(nameof(QcLot), request.LotId);
        }

        if (!lot.IsActive)
        {
            return Error.Rule("qc.lot_inactive", $"Control lot {lot.LotNumber} is inactive.");
        }

        var now = time.GetUtcNow();
        var runAt = request.RunAt ?? now;
        if (runAt > now + DiagnosticsTime.FutureTolerance)
        {
            return Error.Field("runAt", "The run time cannot be in the future.");
        }

        if (lot.IsExpiredOn(DiagnosticsTime.LocalDate(runAt)))
        {
            return Error.Rule("qc.lot_expired",
                $"Control lot {lot.LotNumber} expired on {lot.ExpiryDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)}. Use an in-date lot.");
        }

        // Earlier accepted runs of the same assay on the same analyser, oldest first (rejected runs were acted upon already).
        var history = await db.Set<QcRun>().AsNoTracking()
            .Where(r => r.LabTestId == lot.LabTestId && r.InstrumentCode == lot.InstrumentCode && r.Outcome != QcOutcome.Rejected && r.RunAt < runAt)
            .OrderByDescending(r => r.RunAt)
            .Take(WestgardEvaluator.HistoryDepth)
            .Select(r => r.ZScore)
            .ToListAsync(ct);
        history.Reverse();

        var zScore = lot.ZScore(request.Value);
        var evaluation = WestgardEvaluator.Evaluate(history, zScore);
        var run = new QcRun
        {
            FacilityId = lot.FacilityId,
            QcLotId = lot.Id,
            LabTestId = lot.LabTestId,
            InstrumentCode = lot.InstrumentCode,
            Value = request.Value,
            ZScore = zScore,
            RunAt = runAt,
            Outcome = evaluation.Outcome,
            Violations = [.. evaluation.Violations],
        };
        db.Add(run);
        await db.SaveChangesAsync(ct);

        var testName = await db.Set<LabTest>().Where(t => t.Id == lot.LabTestId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? "the assay";
        if (evaluation.IsRejected)
        {
            var title = $"QC rejected: {testName}";
            var body = $"Level {lot.Level} lot {lot.LotNumber} violated {string.Join(", ", evaluation.Violations)}. Verification of {testName} is locked until QC passes or a supervisor overrides it.";
            var link = $"/lab/qc/lots/{lot.Id}";
            await AfterCommit.RunAsync(logger, "QC alert", () =>
                notifications.NotifyPermissionHoldersAsync(DiagnosticsPermissions.LabQcManage, lot.FacilityId, title, body, link, "diagnostics", "warning", ct));
            await AfterCommit.RunAsync(logger, "QC alert", () =>
                notifications.NotifyPermissionHoldersAsync(DiagnosticsPermissions.LabResultsVerify, lot.FacilityId, title, body, link, "diagnostics", "warning", ct));
        }

        await realtime.PublishAsync(lot.FacilityId, "lab.qc.run", new { run.Id, lot.LabTestId, run.Outcome }, ct);
        var users = await lookups.UserNamesAsync([currentUser.UserId], ct);
        return ToDto(run, lot, users, overridden: false);
    }

    public async Task<IReadOnlyList<QcLockout>> LockoutsAsync(Guid facilityId, IReadOnlyCollection<Guid> testIds, CancellationToken ct) =>
        (await TodayAsync(facilityId, testIds, ct)).Lockouts;

    public async Task<IReadOnlySet<Guid>> AcceptedTestsAsync(Guid facilityId, IReadOnlyCollection<Guid> testIds, CancellationToken ct) =>
        (await TodayAsync(facilityId, testIds, ct)).AcceptedTestIds;

    /// <summary>
    /// Records an audited supervisor override for every rejected run that is locking the given analytes. Runs in the
    /// caller's unit of work, because verification with an override reason is one transaction.
    /// </summary>
    public async Task<int> RecordOverridesAsync(IReadOnlyCollection<QcLockout> lockouts, string reason, CancellationToken ct)
    {
        var runIds = lockouts.SelectMany(l => l.RejectedRunIds).Distinct().ToList();
        var runs = await db.Set<QcRun>().AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.FacilityId, r.LabTestId, r.Violations })
            .ToListAsync(ct);
        var now = time.GetUtcNow();
        foreach (var run in runs)
        {
            db.Add(new QcOverride
            {
                FacilityId = run.FacilityId,
                LabTestId = run.LabTestId,
                QcRunId = run.Id,
                Reason = reason.Trim(),
                OverriddenAt = now,
                OverriddenBy = currentUser.UserId,
            });
            audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.qc.override", $"QC lockout overridden: {reason.Trim()}")
            {
                EntityType = nameof(QcRun),
                EntityId = run.Id.ToString(),
                Severity = AuditSeverity.Warning,
                Details = new { run.LabTestId, violations = run.Violations, reason = reason.Trim() },
            });
        }

        return runs.Count;
    }

    /// <summary>Stand-alone override (from the QC screen) for one analyte.</summary>
    public async Task<Result<int>> OverrideAsync(QcOverrideRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var lockouts = await LockoutsAsync(facilityId, [request.LabTestId], ct);
        if (lockouts.Count == 0)
        {
            return Error.Rule("qc.not_locked", "QC is not locking this test today.");
        }

        var count = await RecordOverridesAsync(lockouts, request.Reason, ct);
        await db.SaveChangesAsync(ct);
        await realtime.PublishAsync(facilityId, "lab.qc.override", new { request.LabTestId }, ct);
        return count;
    }

    public async Task<Result<QcRunDto>> ReviewAsync(Guid runId, ReviewQcRunRequest request, CancellationToken ct)
    {
        var run = await db.Set<QcRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(QcRun), runId);
        }

        var reviewed = run.Review(request.Note, request.CorrectiveAction, currentUser.UserId, time.GetUtcNow());
        if (reviewed.IsFailure)
        {
            return reviewed.Error!;
        }

        await db.SaveChangesAsync(ct);
        var lot = await db.Set<QcLot>().AsNoTracking().FirstAsync(l => l.Id == run.QcLotId, ct);
        var overridden = await db.Set<QcOverride>().AnyAsync(o => o.QcRunId == run.Id, ct);
        var users = await lookups.UserNamesAsync([run.CreatedBy, run.ReviewedBy], ct);
        return ToDto(run, lot, users, overridden);
    }

    public async Task<Result<LeveyJenningsChart>> LeveyJenningsAsync(Guid lotId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var lot = await db.Set<QcLot>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == lotId, ct);
        if (lot is null)
        {
            return Error.NotFound(nameof(QcLot), lotId);
        }

        var today = DiagnosticsTime.Today(time);
        var (start, end) = DiagnosticsTime.Range(from ?? today.AddDays(-30), to ?? today, time, maxDays: 366);
        var points = await db.Set<QcRun>().AsNoTracking()
            .Where(r => r.QcLotId == lotId && r.RunAt >= start && r.RunAt < end)
            .OrderBy(r => r.RunAt)
            .Select(r => new LeveyJenningsPoint(r.Id, r.RunAt, r.Value, r.ZScore, r.Outcome, r.Violations))
            .ToListAsync(ct);
        var testName = await db.Set<LabTest>().Where(t => t.Id == lot.LabTestId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? string.Empty;

        LimitLine Line(string label, int sds) => new(label, lot.TargetMean + (sds * lot.TargetSd));
        IReadOnlyList<LimitLine> limits = [Line("-3SD", -3), Line("-2SD", -2), Line("-1SD", -1), Line("Mean", 0), Line("+1SD", 1), Line("+2SD", 2), Line("+3SD", 3)];
        return new LeveyJenningsChart(lot.Id, testName, lot.Material, lot.Level, lot.LotNumber, lot.Unit, lot.TargetMean, lot.TargetSd, limits, points);
    }

    /// <summary>Today's QC board: every active, in-date lot with its latest run, lock and override state.</summary>
    public async Task<IReadOnlyList<QcStatusRow>> StatusAsync(Guid? facilityId, CancellationToken ct)
    {
        var today = DiagnosticsTime.Today(time);
        var dayStart = DiagnosticsTime.StartOfDayUtc(today);
        var lots = await (
                from l in db.Set<QcLot>().AsNoTracking()
                join t in db.Set<LabTest>() on l.LabTestId equals t.Id
                where l.IsActive && l.ExpiryDate >= today && (facilityId == null || l.FacilityId == facilityId)
                orderby t.Name, l.Level
                select new { l.Id, l.LabTestId, t.Code, t.Name, l.LotNumber, l.Level, l.InstrumentCode })
            .ToListAsync(ct);
        var lotIds = lots.Select(l => l.Id).ToList();
        var runs = await db.Set<QcRun>().AsNoTracking()
            .Where(r => lotIds.Contains(r.QcLotId) && r.RunAt >= dayStart)
            .Select(r => new { r.Id, r.QcLotId, r.RunAt, r.Outcome })
            .ToListAsync(ct);
        var latest = runs.GroupBy(r => r.QcLotId).ToDictionary(g => g.Key, g => g.MaxBy(r => r.RunAt)!);
        var rejectedIds = latest.Values.Where(r => r.Outcome == QcOutcome.Rejected).Select(r => r.Id).ToList();
        var overridden = rejectedIds.Count == 0
            ? []
            : await db.Set<QcOverride>().AsNoTracking().Where(o => rejectedIds.Contains(o.QcRunId)).Select(o => o.QcRunId).ToListAsync(ct);

        return lots.Select(l =>
        {
            var run = latest.GetValueOrDefault(l.Id);
            var isOverridden = run is not null && overridden.Contains(run.Id);
            return new QcStatusRow(
                l.LabTestId, l.Code, l.Name, l.Id, l.LotNumber, l.Level, l.InstrumentCode, run?.Outcome, run?.RunAt,
                run?.Outcome == QcOutcome.Rejected && !isOverridden, isOverridden);
        }).ToList();
    }

    internal static QcRunDto ToDto(QcRun run, QcLot lot, IReadOnlyDictionary<Guid, string> users, bool overridden) => new(
        run.Id, run.QcLotId, run.LabTestId, lot.LotNumber, lot.Level, run.InstrumentCode, run.Value, run.ZScore, run.RunAt, run.Outcome,
        run.Violations, run.CreatedBy is { } by ? users.GetValueOrDefault(by) : null, run.ReviewedAt,
        run.ReviewedBy is { } reviewer ? users.GetValueOrDefault(reviewer) : null, run.ReviewNote, run.CorrectiveAction, overridden);

    /// <summary>
    /// Today's latest run per control lot for the assays (QC groups) of the given analytes. A lot whose latest run is
    /// rejected and not overridden locks the assay. The assay counts as accepted only when it has runs today and none of
    /// the lots' latest runs is rejected.
    /// </summary>
    private async Task<QcDayState> TodayAsync(Guid facilityId, IReadOnlyCollection<Guid> testIds, CancellationToken ct)
    {
        if (testIds.Count == 0)
        {
            return new QcDayState([], new HashSet<Guid>());
        }

        var ids = testIds.Distinct().ToList();
        var tests = await db.Set<LabTest>().AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, Key = t.QcGroup ?? t.Code })
            .ToListAsync(ct);
        var keys = tests.Select(t => t.Key).Distinct().ToList();
        var related = await db.Set<LabTest>().AsNoTracking()
            .Where(t => keys.Contains(t.QcGroup ?? t.Code))
            .Select(t => new { t.Id, Key = t.QcGroup ?? t.Code })
            .ToListAsync(ct);
        var keyByTest = related.ToDictionary(r => r.Id, r => r.Key);
        var relatedIds = related.Select(r => r.Id).ToList();

        var dayStart = DiagnosticsTime.StartOfDayUtc(DiagnosticsTime.Today(time));
        var runs = await db.Set<QcRun>().AsNoTracking()
            .Where(r => r.FacilityId == facilityId && relatedIds.Contains(r.LabTestId) && r.RunAt >= dayStart)
            .Select(r => new QcRunFact(r.Id, r.LabTestId, r.QcLotId, r.RunAt, r.Outcome, r.Violations))
            .ToListAsync(ct);
        var latestPerLot = runs.GroupBy(r => r.QcLotId).Select(g => g.MaxBy(r => r.RunAt)!).ToList();

        var rejectedIds = latestPerLot.Where(r => r.Outcome == QcOutcome.Rejected).Select(r => r.Id).ToList();
        var overridden = rejectedIds.Count == 0
            ? []
            : await db.Set<QcOverride>().AsNoTracking().Where(o => rejectedIds.Contains(o.QcRunId)).Select(o => o.QcRunId).ToListAsync(ct);

        var lockouts = new List<QcLockout>();
        var accepted = new HashSet<Guid>();
        foreach (var test in tests)
        {
            var assayRuns = latestPerLot.Where(r => keyByTest.GetValueOrDefault(r.LabTestId) == test.Key).ToList();
            var blocking = assayRuns.Where(r => r.Outcome == QcOutcome.Rejected && !overridden.Contains(r.Id)).ToList();
            if (blocking.Count > 0)
            {
                lockouts.Add(new QcLockout(test.Id, test.Name, blocking.Select(r => r.Id).ToList(), blocking.SelectMany(r => r.Violations).Distinct().ToList()));
            }

            if (assayRuns.Count > 0 && assayRuns.All(r => r.Outcome != QcOutcome.Rejected))
            {
                accepted.Add(test.Id);
            }
        }

        return new QcDayState(lockouts, accepted);
    }
}
