using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;

/// <summary>
/// /api/v1/lab/orders — accession lists and detail, walk-in registration, cancellation and charge retry (LIS-02);
/// cumulative patient results, TAT (LIS-07) and the critical call register (LIS-05).
/// </summary>
internal static class LabOrderEndpoints
{
    // Enums are stored as text, so priority and status sort by an explicit rank (urgency, workflow order), not alphabetically.
    private static readonly Dictionary<string, Expression<Func<LabOrder, object?>>> OrderSort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orderedAt"] = o => o.OrderedAt,
        ["accessionNumber"] = o => o.AccessionNumber,
        ["priority"] = o => o.Priority == OrderPriority.Stat ? 2 : o.Priority == OrderPriority.Urgent ? 1 : 0,
        ["status"] = o => o.Status == LabOrderStatus.Ordered ? 0
            : o.Status == LabOrderStatus.InProgress ? 1
            : o.Status == LabOrderStatus.Completed ? 2
            : 3,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/lab/orders").WithTags("Laboratory");
        orders.MapGet("/", List).RequirePermission(DiagnosticsPermissions.LabOrdersRead);
        orders.MapGet("/{id:guid}", Get).RequirePermission(DiagnosticsPermissions.LabOrdersRead);
        orders.MapPost("/", Create).RequirePermission(DiagnosticsPermissions.LabOrdersCreate).WithValidation<CreateLabOrderRequest>();
        orders.MapPost("/{id:guid}/charges/retry", RetryCharges).RequirePermission(DiagnosticsPermissions.LabOrdersCreate);
        orders.MapPost("/items/{itemId:guid}/cancel", CancelItem).RequirePermission(DiagnosticsPermissions.LabOrdersCreate).WithValidation<CancelLabItemRequest>();

        var lab = app.MapGroup("/api/v1/lab").WithTags("Laboratory");
        lab.MapGet("/patients/{patientId:guid}/history", History).RequirePermission(DiagnosticsPermissions.LabOrdersRead);
        lab.MapGet("/tat", Tat).RequirePermission(DiagnosticsPermissions.LabOrdersRead);
        lab.MapGet("/critical-calls", CriticalCalls)
            .RequireAnyPermission(DiagnosticsPermissions.LabOrdersRead, DiagnosticsPermissions.RadiologyRead, DiagnosticsPermissions.CardiologyRead);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        DateOnly? from,
        DateOnly? to,
        LabOrderStatus? status,
        OrderPriority? priority,
        PatientClass? patientClass,
        Guid? patientId,
        HospitalDbContext db,
        DiagnosticsLookups lookups,
        IFieldProtector protector,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var orders = db.Set<LabOrder>().AsNoTracking();

        // A patient's own list spans all dates unless a range is asked for; the lab's lists are per day/range.
        if (patientId is { } pid)
        {
            orders = orders.Where(o => o.PatientId == pid);
        }

        if (patientId is null || from is not null || to is not null)
        {
            var (start, end) = DiagnosticsTime.Range(from, to, time);
            orders = orders.Where(o => o.OrderedAt >= start && o.OrderedAt < end);
        }

        if (status is not null)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (priority is not null)
        {
            orders = orders.Where(o => o.Priority == priority);
        }

        if (patientClass is not null)
        {
            orders = orders.Where(o => o.PatientClass == patientClass);
        }

        if (page.SearchTerm is { } term)
        {
            var accession = term.ToUpperInvariant();
            var matching = db.Set<Patient>().Search(term, protector).Select(p => p.Id);
            orders = orders.Where(o => o.AccessionNumber == accession || matching.Contains(o.PatientId));
        }

        var paged = await orders.OrderByKey(page, OrderSort, o => o.OrderedAt, defaultDescending: true)
            .Select(o => new { o.Id, o.AccessionNumber, o.PatientId, o.PatientClass, o.Source, o.Priority, o.Status, o.OrderedAt })
            .ToPagedResultAsync(page, ct);

        var ids = paged.Items.Select(o => o.Id).ToList();
        var patients = await lookups.PatientsAsync(paged.Items.Select(o => o.PatientId).Distinct().ToList(), ct);
        var items = await db.Set<LabOrderItem>().AsNoTracking()
            .Where(i => ids.Contains(i.LabOrderId) && i.Status != LabOrderItemStatus.Cancelled)
            .Select(i => new { i.LabOrderId, i.TestName, i.Status })
            .ToListAsync(ct);
        var reports = await db.Set<LabReport>().AsNoTracking()
            .Where(r => ids.Contains(r.LabOrderId))
            .Select(r => new { r.LabOrderId, r.Id, r.Status })
            .ToListAsync(ct);
        var today = DiagnosticsTime.Today(time);

        return TypedResults.Ok(paged.Map(o =>
        {
            var patient = patients.GetValueOrDefault(o.PatientId);
            var tests = items.Where(i => i.LabOrderId == o.Id).ToList();
            var report = reports.FirstOrDefault(r => r.LabOrderId == o.Id);
            return new LabOrderListItem(
                o.Id, o.AccessionNumber, o.PatientId, patient?.Uhid ?? string.Empty, patient?.FullName ?? string.Empty, patient?.Gender ?? Gender.Unknown,
                patient?.AgeOn(today), o.PatientClass, o.Source, o.Priority, o.Status, o.OrderedAt, tests.Count,
                tests.Count(i => i.Status == LabOrderItemStatus.Released), tests.Select(i => i.TestName).ToList(), report?.Id, report?.Status);
        }));
    }

    private static async Task<IResult> Get(Guid id, LabOrderService service, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var detail = await service.DetailAsync(id, ct);
        if (detail is null)
        {
            return Error.NotFound(nameof(LabOrder), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(detail.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        await audit.PatientRecordAccessedAsync(detail.PatientId, $"Viewed lab order {detail.AccessionNumber}", ct);
        return TypedResults.Ok(detail);
    }

    private static async Task<IResult> Create(CreateLabOrderRequest request, LabOrderService service, CancellationToken ct) =>
        (await service.CreateManualAsync(request, ct)).ToCreated(o => $"/api/v1/lab/orders/{o.Id}");

    private static async Task<IResult> RetryCharges(Guid id, LabOrderService service, CancellationToken ct) =>
        (await service.RetryChargesAsync(id, ct)).ToOk();

    private static async Task<IResult> CancelItem(Guid itemId, CancelLabItemRequest request, LabOrderService service, CancellationToken ct) =>
        (await service.CancelItemAsync(itemId, request.Reason, ct)).ToNoContent();

    /// <summary>Cumulative (trend) report of released results for a patient, grouped per analyte.</summary>
    private static async Task<IResult> History(
        Guid patientId, Guid? labTestId, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var results = db.Set<LabResult>().AsNoTracking().Where(r => r.PatientId == patientId && r.Status == LabResultStatus.Verified);
        if (labTestId is not null)
        {
            results = results.Where(r => r.LabTestId == labTestId);
        }

        var rows = await (
                from r in results
                join i in db.Set<LabOrderItem>() on r.LabOrderItemId equals i.Id
                join o in db.Set<LabOrder>() on r.LabOrderId equals o.Id
                where i.Status == LabOrderItemStatus.Released
                orderby r.VerifiedAt descending
                select new { r.Id, r.LabTestId, r.TestName, r.Unit, r.LabOrderId, o.AccessionNumber, r.VerifiedAt, r.ValueText, r.NumericValue, r.Flag, r.ReferenceText })
            .Take(1000)
            .ToListAsync(ct);

        await audit.PatientRecordAccessedAsync(patientId, "Viewed cumulative lab results", ct);
        return TypedResults.Ok(rows
            .GroupBy(r => r.LabTestId)
            .Select(g => new PatientLabHistory(
                patientId, g.Key, g.First().TestName, g.First().Unit,
                g.OrderBy(r => r.VerifiedAt).Select(r => new PatientLabHistoryPoint(
                    r.Id, r.LabOrderId, r.AccessionNumber, r.VerifiedAt!.Value, r.ValueText, r.NumericValue, r.Unit, r.Flag, r.ReferenceText)).ToList()))
            .OrderBy(h => h.TestName)
            .ToList());
    }

    /// <summary>Turnaround per test (order → release and receipt → verification) with the share released within target.</summary>
    private static async Task<IResult> Tat(DateOnly? from, DateOnly? to, LabSection? section, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (start, end) = DiagnosticsTime.Range(from, to, time);
        var rows = await (
                from i in db.Set<LabOrderItem>().AsNoTracking()
                join o in db.Set<LabOrder>() on i.LabOrderId equals o.Id
                join t in db.Set<LabTest>() on i.LabTestId equals t.Id
                join s in db.Set<LabSample>() on i.SampleId equals (Guid?)s.Id into tubes
                from s in tubes.DefaultIfEmpty()
                where o.OrderedAt >= start && o.OrderedAt < end && i.Status != LabOrderItemStatus.Cancelled && (section == null || i.Section == section)
                select new
                {
                    i.LabTestId, t.Code, t.Name, i.Section, t.TatHours, o.OrderedAt,
                    ReceivedAt = s != null ? s.ReceivedAt : null, i.VerifiedAt, i.ReleasedAt,
                })
            .ToListAsync(ct);

        var tests = rows.GroupBy(r => r.LabTestId).Select(g =>
        {
            var first = g.First();
            var released = g.Where(r => r.ReleasedAt != null).ToList();
            var within = released.Count(r => (r.ReleasedAt!.Value - r.OrderedAt).TotalHours <= first.TatHours);
            return new LabTatRow(
                g.Key, first.Code, first.Name, first.Section, first.TatHours, released.Count, within,
                released.Count == 0 ? null : Math.Round(100.0 * within / released.Count, 1),
                TatStatistics.Summarize(released.Select(r => TatStatistics.Minutes(r.OrderedAt, r.ReleasedAt)).OfType<double>()),
                TatStatistics.Summarize(g.Select(r => TatStatistics.Minutes(r.ReceivedAt, r.VerifiedAt)).OfType<double>()));
        }).OrderBy(r => r.Section).ThenBy(r => r.TestName).ToList();

        var samples = await db.Set<LabSample>().AsNoTracking().CountAsync(s => s.CollectedAt >= start && s.CollectedAt < end, ct);
        var rejections = await db.Set<LabSample>().AsNoTracking()
            .Where(s => s.RejectedAt >= start && s.RejectedAt < end && s.RejectionReason != null)
            .GroupBy(s => s.RejectionReason!.Value)
            .Select(g => new RejectionStat(g.Key, g.Count()))
            .ToListAsync(ct);

        return TypedResults.Ok(new LabTatReport(
            DiagnosticsTime.LocalDate(start),
            DiagnosticsTime.LocalDate(end).AddDays(-1),
            TatStatistics.Summarize(rows.Select(r => TatStatistics.Minutes(r.OrderedAt, r.ReleasedAt)).OfType<double>()),
            tests,
            samples,
            rejections.Sum(r => r.Count),
            rejections.OrderByDescending(r => r.Count).ToList()));
    }

    /// <summary>Critical call register (NABH): every documented critical communication in the period.</summary>
    private static async Task<IResult> CriticalCalls(
        [AsParameters] PageQuery query, DateOnly? from, DateOnly? to, DiagnosticKind? kind, HospitalDbContext db, DiagnosticsLookups lookups, TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var (start, end) = DiagnosticsTime.Range(from, to, time);
        var calls = db.Set<CriticalCallLog>().AsNoTracking().Where(c => c.InformedAt >= start && c.InformedAt < end);
        if (kind is not null)
        {
            calls = calls.Where(c => c.Kind == kind);
        }

        var paged = await calls.OrderByDescending(c => c.InformedAt).ToPagedResultAsync(page, ct);
        var patients = await lookups.PatientsAsync(paged.Items.Select(c => c.PatientId).Distinct().ToList(), ct);
        var users = await lookups.UserNamesAsync(paged.Items.Select(c => c.CalledByUserId), ct);
        return TypedResults.Ok(paged.Map(c => new CriticalCallDto(
            c.Id, c.Kind, c.PatientId, patients.GetValueOrDefault(c.PatientId)?.Uhid ?? string.Empty, patients.GetValueOrDefault(c.PatientId)?.FullName ?? string.Empty,
            c.Finding, c.InformedPersonName, c.InformedPersonRole, c.Method, c.InformedAt, c.ReadBackConfirmed,
            c.CalledByUserId is { } by ? users.GetValueOrDefault(by) : null, c.Notes, c.LabResultId, c.ImagingStudyId, c.CardiologyStudyId)));
    }
}
