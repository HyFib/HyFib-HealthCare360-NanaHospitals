using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Report release and amendment (LIS-02, LIS-07). Release is per test: every verified test not yet released goes out
/// together, raising DiagnosticReportReleased for the EHR and queueing <c>lab.report_ready</c> to the patient.
/// A correction after release is a numbered amendment with a reason, printed on the report and audited. A corrected
/// value that is critical needs a new documented call.
/// </summary>
internal sealed class LabReportService(
    HospitalDbContext db,
    ResultEvaluator evaluator,
    ReportDelivery delivery,
    CriticalValueNotifier critical,
    DiagnosticsLookups lookups,
    DiagnosticsRealtime realtime,
    NotifiableDiseases notifiable,
    AntimicrobialCultureReview stewardship,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<LabReportService> logger)
{
    public async Task<Result<ReleaseOutcome>> ReleaseAsync(Guid reportId, CancellationToken ct)
    {
        var report = await db.Set<LabReport>().FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null)
        {
            return Error.NotFound(nameof(LabReport), reportId);
        }

        var outcome = await ReleaseCoreAsync(report, ct);
        if (outcome.IsFailure)
        {
            return outcome;
        }

        await db.SaveChangesAsync(ct);
        await AnnounceReleasedAsync([outcome.Value], ct);
        return outcome;
    }

    /// <summary>Releases whatever is verified on the given accessions (used by "verify and release"); does not save.</summary>
    internal async Task<IReadOnlyList<ReleaseOutcome>> ReleaseForOrdersAsync(IReadOnlyCollection<Guid> orderIds, CancellationToken ct)
    {
        var reports = await db.Set<LabReport>().Where(r => orderIds.Contains(r.LabOrderId)).ToListAsync(ct);
        var outcomes = new List<ReleaseOutcome>();
        foreach (var report in reports)
        {
            var outcome = await ReleaseCoreAsync(report, ct);
            if (outcome.IsSuccess)
            {
                outcomes.Add(outcome.Value);
            }
        }

        return outcomes;
    }

    /// <summary>After commit: refresh screens showing these reports.</summary>
    internal async Task AnnounceReleasedAsync(IReadOnlyCollection<ReleaseOutcome> released, CancellationToken ct)
    {
        var ids = released.Select(r => r.ReportId).ToList();
        var facilities = await db.Set<LabReport>().AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.FacilityId, r.LabOrderId }).ToListAsync(ct);
        foreach (var report in facilities)
        {
            await realtime.PublishAsync(report.FacilityId, "lab.report.released", new { ReportId = report.Id, report.LabOrderId }, ct);
        }
    }

    public async Task<Result> AmendAsync(Guid reportId, AmendReportRequest request, CancellationToken ct)
    {
        var report = await db.Set<LabReport>().FirstOrDefaultAsync(r => r.Id == reportId, ct);
        if (report is null)
        {
            return Error.NotFound(nameof(LabReport), reportId);
        }

        var ids = request.Changes.Select(c => c.ResultId).ToList();
        var results = await db.Set<LabResult>().Where(r => ids.Contains(r.Id)).ToListAsync(ct);
        if (results.Count != ids.Count || results.Any(r => r.LabOrderId != report.LabOrderId))
        {
            return Error.Field("changes", "Some of the results do not belong to this report.");
        }

        await db.Set<LabOrder>().Where(o => o.Id == report.LabOrderId).Include(o => o.Items).LoadAsync(ct);
        var order = db.Set<LabOrder>().Local.First(o => o.Id == report.LabOrderId);
        var items = order.Items.ToDictionary(i => i.Id);
        var unreleased = results.Where(r => items.GetValueOrDefault(r.LabOrderItemId)?.Status != LabOrderItemStatus.Released).Select(r => r.TestName).ToList();
        if (unreleased.Count > 0)
        {
            return Error.Rule("lab.result_not_released", $"Only released results are amended; correct these through result entry: {string.Join(", ", unreleased)}.");
        }

        var context = await evaluator.LoadAsync(results, ct);
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var changes = new List<string>();
        var criticalResults = new List<LabResult>();
        for (var i = 0; i < request.Changes.Count; i++)
        {
            var change = request.Changes[i];
            var result = results.First(r => r.Id == change.ResultId);
            var capture = context.Capture(
                result, change.Value, change.Remarks ?? result.Remarks, change.Organism ?? result.Organism, change.ColonyCount ?? result.ColonyCount, result.InstrumentCode);
            if (capture.IsFailure)
            {
                errors[$"changes[{i}].value"] = [capture.Error!.Message];
                continue;
            }

            var before = result.Describe();
            var amended = result.Amend(capture.Value, currentUser.UserId, now);
            if (amended.IsFailure)
            {
                errors[$"changes[{i}].value"] = [amended.Error!.Message];
                continue;
            }

            changes.Add($"{result.TestName}: {before} -> {result.Describe()}");
            if (result.IsCritical)
            {
                criticalResults.Add(result);
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("lab.amendment_invalid", "Some corrections could not be applied.", errors);
        }

        // A corrected critical value is a new communication: the earlier call documented a different value.
        if (criticalResults.Count > 0)
        {
            if (request.CriticalCall is null)
            {
                return Error.Rule("lab.critical_call_required", $"A corrected value is critical ({string.Join(", ", criticalResults.Select(r => r.TestName))}). Document who was informed.");
            }

            foreach (var result in criticalResults)
            {
                var log = CriticalCallLog.Record(
                    new CriticalFindingRef(DiagnosticKind.Laboratory, result.FacilityId, result.PatientId, $"{result.TestName} {result.Describe()} ({result.Flag}) (amended)", LabResultId: result.Id),
                    request.CriticalCall,
                    currentUser.UserId,
                    now);
                if (log.IsFailure)
                {
                    return log.Error!;
                }

                db.Add(log.Value);
            }
        }

        await NotifyAsync(results, now, amended: true, ct);
        var affected = results.Select(r => items[r.LabOrderItemId]).Distinct().ToList();
        var recorded = report.Amend(order, request.Reason.Trim(), string.Join("; ", changes), affected, criticalResults.Count > 0, currentUser.UserId, now);
        if (recorded.IsFailure)
        {
            return recorded;
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.report.amended", $"Amended report {report.ReportNumber}: {request.Reason.Trim()}")
        {
            PatientId = report.PatientId,
            EntityType = nameof(LabReport),
            EntityId = report.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { revision = report.Revision, changes },
        });

        var alerts = criticalResults.Select(r => new CriticalAlert(
            DiagnosticKind.Laboratory, r.FacilityId, r.PatientId, order.OrderingPractitionerId, r.TestName, $"{r.Describe()} ({r.Flag})", $"/lab/orders/{order.Id}")).ToList();
        await critical.QueueDoctorSmsAsync(alerts, ct);
        await db.SaveChangesAsync(ct);

        await critical.NotifyVerifiedAsync(alerts, ct);
        await NotifyOrderingDoctorAsync(order, report, ct);
        await realtime.PublishAsync(report.FacilityId, "lab.report.amended", new { ReportId = report.Id, report.LabOrderId, report.Revision }, ct);
        return Result.Success();
    }

    private async Task<Result<ReleaseOutcome>> ReleaseCoreAsync(LabReport report, CancellationToken ct)
    {
        await db.Set<LabOrder>().Where(o => o.Id == report.LabOrderId).Include(o => o.Items).LoadAsync(ct);
        await db.Set<LabResult>().Where(r => r.LabOrderId == report.LabOrderId).LoadAsync(ct);
        var order = db.Set<LabOrder>().Local.First(o => o.Id == report.LabOrderId);
        var criticalItemIds = db.Set<LabResult>().Local
            .Where(r => r.LabOrderId == order.Id && r.IsCritical)
            .Select(r => r.LabOrderItemId)
            .ToHashSet();

        var released = report.Release(order, criticalItemIds, currentUser.UserId, time.GetUtcNow());
        if (released.IsFailure)
        {
            return released.Error!;
        }

        var releasedItems = released.Value.Select(i => i.Id).ToHashSet();
        await NotifyAsync(db.Set<LabResult>().Local.Where(r => releasedItems.Contains(r.LabOrderItemId) && r.Status == LabResultStatus.Verified), report.ReleasedAt!.Value,
            amended: false, ct);
        await delivery.QueueReportReadyAsync(
            order.PatientId, order.AccessionNumber, new NotificationDocument(LabReportDocumentSource.DocumentKind, report.Id), order.FacilityId, ct);
        return new ReleaseOutcome(report.Id, released.Value.Select(i => i.Id).ToList(), report.IsPartial, report.VerificationCode);
    }

    /// <summary>
    /// A released positive result of a test that confirms a notifiable disease goes on the week's IDSP L form, in the
    /// caller's save; a confirming result amended to negative takes the case off it.
    /// </summary>
    private async Task NotifyAsync(IEnumerable<LabResult> results, DateTimeOffset at, bool amended, CancellationToken ct)
    {
        var list = results.ToList();
        var testIds = list.Select(r => r.LabTestId).Distinct().ToList();
        var loinc = await db.Set<LabTest>().AsNoTracking().Where(t => testIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Loinc, ct);
        foreach (var result in list)
        {
            if (IsPositive(result))
            {
                await notifiable.FromLabResultAsync(
                    new NotifiableLabFinding(
                        result.FacilityId, result.PatientId, result.Id, result.TestCode, loinc.GetValueOrDefault(result.LabTestId), result.TestName,
                        result.Describe(), result.Organism, at),
                    ct);
                if (result.ResultType == LabResultType.Culture && !string.IsNullOrWhiteSpace(result.Organism))
                {
                    // A culture that grew something: the patient's restricted antibiotics fall due for review, with the antibiogram.
                    var antibiogram = await db.Set<LabSusceptibility>().AsNoTracking().Where(s => s.LabResultId == result.Id)
                        .Select(s => new { s.Antibiotic, s.Interpretation }).ToListAsync(ct);
                    await stewardship.CultureReleasedAsync(
                        result.FacilityId, result.PatientId, result.Id, result.Organism,
                        antibiogram.Select(s => (s.Antibiotic, s.Interpretation.ToString())).ToList(), at, ct);
                }
            }
            else if (amended)
            {
                await notifiable.WithdrawLabResultAsync(result.Id, at, ct);
            }
        }
    }

    /// <summary>Flagged outside the normal answers, or a culture that grew an organism.</summary>
    internal static bool IsPositive(LabResult result) =>
        result.Flag is not null and not ResultFlag.Normal || (result.ResultType == LabResultType.Culture && !string.IsNullOrWhiteSpace(result.Organism));

    /// <summary>The doctor may already have acted on the earlier value, so a correction is always pushed to them.</summary>
    private async Task NotifyOrderingDoctorAsync(LabOrder order, LabReport report, CancellationToken ct)
    {
        if (order.OrderingPractitionerId is not { } practitionerId)
        {
            return;
        }

        var users = await lookups.UserIdsForPractitionerAsync(practitionerId, ct);
        await AfterCommit.RunAsync(logger, "amendment alert", () => notifications.NotifyUsersAsync(
            users,
            $"Lab report amended: {report.ReportNumber}",
            $"Revision {report.Revision} of report {report.ReportNumber} corrects released results. Review the changes.",
            $"/lab/reports/{report.Id}",
            "diagnostics",
            "warning",
            ct));
    }
}
