using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;

/// <summary>
/// /api/v1/lab/worklist and /results: the bench worklist, result entry (bulk), pathologist verification with the QC
/// lockout and mandatory critical-call documentation, reopening, and additional critical calls (LIS-02, LIS-04..06).
/// </summary>
internal static class LabResultEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var lab = app.MapGroup("/api/v1/lab").WithTags("Laboratory");
        lab.MapGet("/worklist", Worklist).RequireAnyPermission(DiagnosticsPermissions.LabResultsEnter, DiagnosticsPermissions.LabResultsVerify);
        lab.MapGet("/samples/{id:guid}/results", Sheet)
            .RequireAnyPermission(DiagnosticsPermissions.LabResultsEnter, DiagnosticsPermissions.LabResultsVerify);
        lab.MapPost("/results", Enter).RequirePermission(DiagnosticsPermissions.LabResultsEnter).WithValidation<EnterResultsRequest>();
        lab.MapPost("/results/verify", Verify).RequirePermission(DiagnosticsPermissions.LabResultsVerify).WithValidation<VerifyResultsRequest>();
        lab.MapPost("/results/{id:guid}/reopen", Reopen).RequirePermission(DiagnosticsPermissions.LabResultsVerify).WithValidation<ReopenResultRequest>();
        lab.MapPost("/results/{id:guid}/critical-calls", RecordCall)
            .RequireAnyPermission(DiagnosticsPermissions.LabResultsVerify, DiagnosticsPermissions.LabResultsEnter)
            .WithValidation<CriticalCallDetails>();
    }

    /// <summary>Tubes in the lab by bench: STAT and critical first, then by receipt time.</summary>
    private static async Task<IResult> Worklist(
        [AsParameters] PageQuery query,
        LabSection? section,
        SampleStatus? status,
        OrderPriority? priority,
        HospitalDbContext db,
        DiagnosticsLookups lookups,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var tubes = db.Set<LabSample>().AsNoTracking();
        tubes = status is { } wanted
            ? tubes.Where(s => s.Status == wanted)
            : tubes.Where(s => s.Status == SampleStatus.Received || s.Status == SampleStatus.InProcess);
        if (section is not null)
        {
            tubes = tubes.Where(s => s.Section == section);
        }

        if (priority is not null)
        {
            tubes = tubes.Where(s => s.Priority == priority);
        }

        if (page.SearchTerm is { } term)
        {
            var code = term.ToUpperInvariant();
            tubes = tubes.Where(s => s.Barcode == term || db.Set<LabOrder>().Any(o => o.Id == s.LabOrderId && o.AccessionNumber == code));
        }

        var paged = await tubes
            .OrderByUrgency(s => s.Priority)
            .ThenByDescending(s => db.Set<LabResult>().Any(r => r.SampleId == s.Id && r.Status != LabResultStatus.Verified
                                                               && (r.Flag == ResultFlag.CriticalLow || r.Flag == ResultFlag.CriticalHigh || r.Flag == ResultFlag.Critical)))
            .ThenBy(s => s.ReceivedAt)
            .Select(s => new { s.Id, s.Barcode, s.LabOrderId, s.PatientId, s.Priority, s.Section, s.Status, s.ReceivedAt })
            .ToPagedResultAsync(page, ct);

        var tubeIds = paged.Items.Select(s => (Guid?)s.Id).ToList();
        var counts = await db.Set<LabResult>().AsNoTracking()
            .Where(r => tubeIds.Contains(r.SampleId))
            .GroupBy(r => r.SampleId!.Value)
            .Select(g => new
            {
                SampleId = g.Key,
                Pending = g.Count(r => r.Status == LabResultStatus.Pending),
                Entered = g.Count(r => r.Status == LabResultStatus.Entered),
                Verified = g.Count(r => r.Status == LabResultStatus.Verified),
                Critical = g.Count(r => r.Flag == ResultFlag.CriticalLow || r.Flag == ResultFlag.CriticalHigh || r.Flag == ResultFlag.Critical),
            })
            .ToDictionaryAsync(c => c.SampleId, ct);
        var items = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => tubeIds.Contains(i.SampleId) && i.Status != LabOrderItemStatus.Cancelled)
            .Select(i => new { i.SampleId, i.TestName })
            .ToListAsync(ct);
        var orderIds = paged.Items.Select(s => s.LabOrderId).Distinct().ToList();
        var orders = await db.Set<LabOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.AccessionNumber, o.PatientClass }).ToDictionaryAsync(o => o.Id, ct);
        var patients = await lookups.PatientsAsync(paged.Items.Select(s => s.PatientId).Distinct().ToList(), ct);
        var today = DiagnosticsTime.Today(time);

        return TypedResults.Ok(paged.Map(s =>
        {
            var patient = patients.GetValueOrDefault(s.PatientId);
            var count = counts.GetValueOrDefault(s.Id);
            var order = orders.GetValueOrDefault(s.LabOrderId);
            return new WorklistItem(
                s.Id, s.Barcode, s.LabOrderId, order?.AccessionNumber ?? string.Empty, s.PatientId, patient?.Uhid ?? string.Empty,
                patient?.FullName ?? string.Empty, patient?.AgeOn(today), patient?.Gender ?? Gender.Unknown, order?.PatientClass ?? PatientClass.OutPatient,
                s.Priority, s.Section, s.Status, s.ReceivedAt, count?.Pending ?? 0, count?.Entered ?? 0, count?.Verified ?? 0, count?.Critical ?? 0,
                items.Where(i => i.SampleId == s.Id).Select(i => i.TestName).ToList());
        }));
    }

    private static async Task<IResult> Sheet(Guid id, LabResultService results, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var sheet = await results.SheetAsync(id, ct);
        if (sheet.IsFailure)
        {
            return sheet.Error!.ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(sheet.Value.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(sheet.Value.PatientId, $"Opened lab results of sample {sheet.Value.Barcode}", ct);
        return TypedResults.Ok(sheet.Value);
    }

    private static async Task<IResult> Enter(EnterResultsRequest request, LabResultService results, CancellationToken ct) =>
        (await results.EnterAsync(request, ct)).ToOk();

    private static async Task<IResult> Verify(VerifyResultsRequest request, LabResultService results, CancellationToken ct) =>
        (await results.VerifyAsync(request, ct)).ToOk();

    private static async Task<IResult> Reopen(Guid id, ReopenResultRequest request, LabResultService results, CancellationToken ct) =>
        (await results.ReopenAsync(id, request.Reason, ct)).ToNoContent();

    private static async Task<IResult> RecordCall(Guid id, CriticalCallDetails request, LabResultService results, CancellationToken ct) =>
        (await results.RecordCriticalCallAsync(id, request, ct)).ToCreated(callId => $"/api/v1/lab/critical-calls/{callId}");
}
