using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Services;

/// <summary>
/// Report release and amendment (LIS-02, LIS-07). Release is per test: every verified test not yet released goes out
/// together, raising DiagnosticReportReleased for the EHR and queueing <c>lab.report_ready</c> to the patient.
/// A correction after release is a numbered amendment with a reason, printed on the report and audited. A corrected
/// value that is critical needs a new documented call. A released HIV result outside its normal answers puts the
/// patient's record in the HIV category (R-4), as an HIV diagnosis does.
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
    PatientAccessGuard records,
    INotificationService notifications,
    IApprovals approvals,
    ISettingsService settings,
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

    /// <summary>
    /// Corrects released results. Where the lab requires a second person to verify (R-6,
    /// <see cref="LabResultService.SeparateVerifierSetting"/>), the correction is asked for: nothing on the report changes
    /// until another pathologist approves it (<see cref="LabAmendmentApproval"/>), and until then the report says a
    /// correction is waiting. A lab with one pathologist makes it at once.
    /// </summary>
    public async Task<Result<AmendmentOutcome>> AmendAsync(Guid reportId, AmendReportRequest request, CancellationToken ct)
    {
        var staged = await StageAmendmentAsync(reportId, request, currentUser.UserId, ct);
        if (staged.IsFailure)
        {
            return staged.Error!;
        }

        var amendment = staged.Value;
        var report = amendment.Report;
        if (await settings.GetAsync<bool?>(LabResultService.SeparateVerifierSetting, report.FacilityId, ct) == true)
        {
            // Checked exactly as it would be made, and none of it kept: it is made again, on the report as it is then,
            // when another pathologist approves it.
            db.ChangeTracker.Clear();
            var asked = await approvals.RequestAsync(
                LabAmendmentApproval.Key,
                new ApprovalSubject(nameof(LabReport), report.Id, report.Version, report.FacilityId, report.PatientId),
                new LabAmendmentPayload(report.Id, request),
                $"Correction of lab report {report.ReportNumber}",
                [
                    new("Report", report.ReportNumber),
                    .. amendment.Changes.Select(c => new ApprovalDetail(c.Test, c.Change)),
                    .. amendment.Calls.Select(c => new ApprovalDetail("Critical value told to", $"{c.InformedPersonName}, {DocumentFormat.DateTime(c.InformedAt)}")),
                ],
                request.Reason,
                ct);
            return asked.IsFailure ? asked.Error! : new AmendmentOutcome(asked.Value);
        }

        var alerts = await RecordAmendmentAsync(amendment, request.Reason, requestedBy: null, ct);
        if (alerts.IsFailure)
        {
            return alerts.Error!;
        }

        await db.SaveChangesAsync(ct);

        await critical.NotifyVerifiedAsync(alerts.Value, ct);
        await NotifyOrderingDoctorAsync(amendment.Order, report, ct);
        await realtime.PublishAsync(report.FacilityId, "lab.report.amended", new { ReportId = report.Id, report.LabOrderId, report.Revision }, ct);
        return new AmendmentOutcome(null);
    }

    /// <summary>
    /// Makes an approved correction, in the approval's transaction (the approval saves and commits): on the report as it
    /// was when it was asked for, checked again, or not at all. The pathologist who asked and the one who approved are
    /// both named on the amendment; the alerts go out once the approval commits.
    /// </summary>
    internal async Task<Result<string>> AmendApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<LabAmendmentPayload>(approval.Payload);
        // The report as it is now, tracked: the version checked here is the one the save is made against, so a change
        // after this read fails the save rather than slipping past the check.
        var current = await db.Set<LabReport>().FirstOrDefaultAsync(r => r.Id == asked.ReportId, ct);
        if (current is null)
        {
            return Error.NotFound(nameof(LabReport), asked.ReportId);
        }

        if (current.Version != approval.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"Report {current.ReportNumber} changed after this correction was asked for (another correction, or more tests released). Ask again, against the report as it is now.");
        }

        var staged = await StageAmendmentAsync(asked.ReportId, asked.Request, approval.RequestedBy, ct);
        if (staged.IsFailure)
        {
            return staged.Error!;
        }

        var amendment = staged.Value;
        var alerts = await RecordAmendmentAsync(amendment, asked.Request.Reason, (approval.RequestedBy, approval.RequestedByName), ct);
        if (alerts.IsFailure)
        {
            return alerts.Error!;
        }

        await critical.NotifyVerifiedAfterCommitAsync(alerts.Value, ct);
        await NotifyOrderingDoctorAfterCommitAsync(amendment.Order, amendment.Report, ct);
        realtime.PublishAfterCommit(amendment.Report.FacilityId, "lab.report.amended",
            new { ReportId = amendment.Report.Id, amendment.Report.LabOrderId, amendment.Report.Revision });
        return $"Revision {amendment.Report.Revision} of report {amendment.Report.ReportNumber}";
    }

    /// <summary>
    /// Checks a correction and applies it to the tracked results, saving nothing: every result is on this report and
    /// released, every value is valid, and a corrected value that is critical has its documented call
    /// (<paramref name="maker"/> made it). The corrected results are verified by the signed-in pathologist.
    /// </summary>
    private async Task<Result<StagedAmendment>> StageAmendmentAsync(Guid reportId, AmendReportRequest request, Guid? maker, CancellationToken ct)
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

        if (report.Status is not (LabReportStatus.Released or LabReportStatus.Amended))
        {
            return Error.Rule("lab.report_not_released", "Only a released report can be amended. Correct unreleased results through result entry.");
        }

        var context = await evaluator.LoadAsync(results, ct);
        var now = time.GetUtcNow();
        var errors = new Dictionary<string, string[]>();
        var changes = new List<AmendedValue>();
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

            changes.Add(new AmendedValue(result.TestName, $"{before} -> {result.Describe()}"));
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
        var calls = new List<CriticalCallLog>();
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
                    maker,
                    now);
                if (log.IsFailure)
                {
                    return log.Error!;
                }

                calls.Add(log.Value);
            }
        }

        return new StagedAmendment(report, order, items, results, changes, criticalResults, calls, now);
    }

    /// <summary>
    /// Records a staged correction for the caller's save: the call log, the public-health and confidentiality follow-ups,
    /// the numbered amendment and its audit, and the ordering doctor's SMS for a critical value (queued in the save).
    /// Returns the in-app alerts to send once it is committed.
    /// </summary>
    private async Task<Result<IReadOnlyList<CriticalAlert>>> RecordAmendmentAsync(
        StagedAmendment amendment, string reason, (Guid Id, string Name)? requestedBy, CancellationToken ct)
    {
        var (report, order) = (amendment.Report, amendment.Order);
        db.AddRange(amendment.Calls);
        await NotifyAsync(amendment.Results, amendment.At, amended: true, ct);
        var affected = amendment.Results.Select(r => amendment.Items[r.LabOrderItemId]).Distinct().ToList();
        var summary = string.Join("; ", amendment.Changes.Select(c => $"{c.Test}: {c.Change}"));
        var recorded = report.Amend(order, reason.Trim(), summary, affected, amendment.Critical.Count > 0, currentUser.UserId, amendment.At, requestedBy);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        var by = requestedBy is { } maker ? $" (asked for by {maker.Name}, approved by {currentUser.DisplayName ?? currentUser.UserName})" : string.Empty;
        audit.Add(new AuditEntry(AuditCategories.Clinical, "lab.report.amended", $"Amended report {report.ReportNumber}: {reason.Trim()}{by}")
        {
            PatientId = report.PatientId,
            EntityType = nameof(LabReport),
            EntityId = report.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { revision = report.Revision, changes = amendment.Changes.Select(c => $"{c.Test}: {c.Change}").ToList(), requestedBy = requestedBy?.Id },
        });

        var alerts = amendment.Critical.Select(r => new CriticalAlert(
            DiagnosticKind.Laboratory, r.FacilityId, r.PatientId, order.OrderingPractitionerId, r.TestName, $"{r.Describe()} ({r.Flag})", $"/lab/orders/{order.Id}")).ToList();
        await critical.QueueDoctorSmsAsync(alerts, ct);
        return alerts;
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
    /// caller's save; a confirming result amended to negative takes the case off it. A positive HIV result puts the
    /// patient's record in the HIV category, in the same save; an amendment to negative leaves it there.
    /// </summary>
    private async Task NotifyAsync(IEnumerable<LabResult> results, DateTimeOffset at, bool amended, CancellationToken ct)
    {
        var list = results.ToList();
        // The test each result's item ordered, with every test under it: a result reported under an HIV test (a band of an
        // "HIV confirmation" panel) is an HIV result, whatever its own name.
        var itemIds = list.Select(r => r.LabOrderItemId).Distinct().ToList();
        var orderedTests = await db.Set<LabOrderItem>().AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.LabTestId, ct);
        var tests = await LabWorkflow.TestsUnderAsync(db, [.. list.Select(r => r.LabTestId), .. orderedTests.Values], ct);
        foreach (var result in list)
        {
            if (IsPositive(result))
            {
                var test = tests.GetValueOrDefault(result.LabTestId);
                await notifiable.FromLabResultAsync(
                    new NotifiableLabFinding(
                        result.FacilityId, result.PatientId, result.Id, result.TestCode, test?.Loinc, result.TestName,
                        result.Describe(), result.Organism, at),
                    ct);
                var ordered = orderedTests.TryGetValue(result.LabOrderItemId, out var orderedId) ? tests.GetValueOrDefault(orderedId) : null;
                if (HivTestConsent.IsHivResult(ordered, result.LabTestId, tests))
                {
                    // A record of HIV status, which the HIV and AIDS Act 2017 keeps confidential (s.8 to 11): the chart opens only
                    // to staff holding the HIV category, as after an HIV diagnosis. The lab's own screens are not affected.
                    await records.FlagAsync(result.PatientId, SensitiveCategory.Hiv, $"an HIV test result ({result.TestCode})", ct);
                }

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
        var (title, body) = AmendedWords(report);
        await AfterCommit.RunAsync(logger, "amendment alert", () => notifications.NotifyUsersAsync(
            users, title, body, $"/lab/reports/{report.Id}", "diagnostics", "warning", ct));
    }

    /// <summary>The same alert as <see cref="NotifyOrderingDoctorAsync"/>, sent only if the caller's transaction commits.</summary>
    private async Task NotifyOrderingDoctorAfterCommitAsync(LabOrder order, LabReport report, CancellationToken ct)
    {
        if (order.OrderingPractitionerId is not { } practitionerId)
        {
            return;
        }

        var users = await lookups.UserIdsForPractitionerAsync(practitionerId, ct);
        var (title, body) = AmendedWords(report);
        realtime.AlertUsersAfterCommit(new DeferredUserAlert(users, report.FacilityId, title, body, $"/lab/reports/{report.Id}", "warning"));
    }

    private static (string Title, string Body) AmendedWords(LabReport report) =>
        ($"Lab report amended: {report.ReportNumber}",
            $"Revision {report.Revision} of report {report.ReportNumber} corrects released results. Review the changes.");

    /// <summary>One corrected value, in words: the test and "old -> new".</summary>
    private sealed record AmendedValue(string Test, string Change);

    /// <summary>A correction checked and applied to the tracked results, not yet recorded or saved.</summary>
    private sealed record StagedAmendment(
        LabReport Report,
        LabOrder Order,
        IReadOnlyDictionary<Guid, LabOrderItem> Items,
        IReadOnlyList<LabResult> Results,
        IReadOnlyList<AmendedValue> Changes,
        IReadOnlyList<LabResult> Critical,
        IReadOnlyList<CriticalCallLog> Calls,
        DateTimeOffset At);
}
