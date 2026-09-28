using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Maintenance;

/// <summary>/api/v1/assets — preventive maintenance, breakdown tickets, calibration and the "due" list (AST-03/04/05).</summary>
internal static class MaintenanceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/assets").WithTags("Assets");

        group.MapGet("/maintenance/due", Due).RequireAnyPermission(SupplyChainPermissions.AssetReaders);

        group.MapGet("/pm-plans", Plans).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapPost("/{assetId:guid}/pm-plans", async (Guid assetId, SavePmPlanRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.SavePlanAsync(assetId, null, r, ct)).ToCreated(id => $"/api/v1/assets/pm-plans/{id}"))
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance).WithValidation<SavePmPlanRequest>();
        group.MapPut("/{assetId:guid}/pm-plans/{planId:guid}", async (Guid assetId, Guid planId, SavePmPlanRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.SavePlanAsync(assetId, planId, r, ct)).ToHttpResult(_ => TypedResults.NoContent()))
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance).WithValidation<SavePmPlanRequest>();
        group.MapPost("/pm-plans/{planId:guid}/visits", async (Guid planId, RecordPmVisitRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.RecordVisitAsync(planId, r, ct)).ToCreated(id => $"/api/v1/assets/pm-visits/{id}"))
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance).WithValidation<RecordPmVisitRequest>();
        group.MapGet("/{assetId:guid}/pm-visits", Visits).RequireAnyPermission(SupplyChainPermissions.AssetReaders);

        group.MapGet("/tickets", Tickets).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapGet("/tickets/{id:guid}", Ticket).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapPost("/tickets", async (ReportBreakdownRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.ReportBreakdownAsync(r, ct)).ToCreated(d => $"/api/v1/assets/tickets/{d.Id}"))
            .RequireAnyPermission(SupplyChainPermissions.AssetReaders).WithValidation<ReportBreakdownRequest>();
        group.MapPost("/tickets/{id:guid}/assign", async (Guid id, AssignTicketRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.AssignAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance).WithValidation<AssignTicketRequest>();
        group.MapPost("/tickets/{id:guid}/start", async (Guid id, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.StartAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance);
        group.MapPost("/tickets/{id:guid}/resolve", async (Guid id, ResolveTicketRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.ResolveAsync(id, r, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance).WithValidation<ResolveTicketRequest>();
        group.MapPost("/tickets/{id:guid}/close", async (Guid id, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.CloseAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance);
        group.MapPost("/tickets/{id:guid}/reopen", async (Guid id, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.ReopenAsync(id, ct)).ToNoContent())
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance);

        group.MapGet("/{assetId:guid}/calibrations", Calibrations).RequireAnyPermission(SupplyChainPermissions.AssetReaders);
        group.MapPost("/{assetId:guid}/calibrations", async (Guid assetId, RecordCalibrationRequest r, MaintenanceService maintenance, CancellationToken ct) =>
                (await maintenance.RecordCalibrationAsync(assetId, r, ct)).ToCreated(id => $"/api/v1/assets/{assetId}/calibrations/{id}"))
            .RequirePermission(SupplyChainPermissions.AssetsMaintenance).WithValidation<RecordCalibrationRequest>();
    }

    private static async Task<IResult> Due(int? days, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var window = Math.Clamp(days ?? MaintenanceService.DueWindowDays, 0, 90);
        var items = await MaintenanceService.DueItemsAsync(db, today, window, Math.Max(window, MaintenanceService.ExpiryWindowDays), ct);
        return TypedResults.Ok(items.Where(i => user.FacilityId is null || i.FacilityId == user.FacilityId).ToList());
    }

    private static async Task<IResult> Plans(Guid? assetId, bool? dueOnly, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var limit = today.AddDays(MaintenanceService.DueWindowDays);
        var plans = db.Set<PreventiveMaintenancePlan>().AsNoTracking();
        if (assetId is not null)
        {
            plans = plans.Where(p => p.AssetId == assetId);
        }

        if (dueOnly == true)
        {
            plans = plans.Where(p => p.IsActive && p.NextDueDate <= limit);
        }

        var rows = await (
                from p in plans
                join a in db.Set<Asset>() on p.AssetId equals a.Id
                orderby p.NextDueDate
                select new { Plan = p, a.AssetTag, a.Name })
            .Take(500)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(r => new PmPlanDto(
            r.Plan.Id, r.Plan.AssetId, r.AssetTag, r.Name, r.Plan.Title, r.Plan.FrequencyDays, r.Plan.NextDueDate, r.Plan.LastDoneOn, r.Plan.Checklist,
            r.Plan.AssignedTo, r.Plan.VendorId, r.Plan.IsActive, r.Plan.NextDueDate.DayNumber - today.DayNumber, r.Plan.Version)).ToList());
    }

    private static async Task<IResult> Visits(Guid assetId, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<PmVisit>().AsNoTracking()
            .Where(v => v.AssetId == assetId)
            .OrderByDescending(v => v.VisitDate)
            .Select(v => new PmVisitDto(v.Id, v.PlanId, v.AssetId, v.VisitDate, v.PerformedBy, v.Outcome, v.Findings, v.Cost, v.NextDueDate, v.ServiceReportFileId))
            .ToListAsync(ct));

    private static async Task<IResult> Tickets(
        [AsParameters] PageQuery query, TicketStatus? status, TicketPriority? priority, Guid? assetId, bool? openOnly, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var tickets = db.Set<MaintenanceTicket>().AsNoTracking();
        if (status is not null)
        {
            tickets = tickets.Where(t => t.Status == status);
        }

        if (openOnly == true)
        {
            tickets = tickets.Where(t => t.Status == TicketStatus.Open || t.Status == TicketStatus.Assigned || t.Status == TicketStatus.InProgress);
        }

        if (priority is not null)
        {
            tickets = tickets.Where(t => t.Priority == priority);
        }

        if (assetId is not null)
        {
            tickets = tickets.Where(t => t.AssetId == assetId);
        }

        if (page.SearchTerm is { } term)
        {
            var upper = term.ToUpperInvariant();
            tickets = tickets.Where(t => t.Number == upper || db.Set<Asset>().Any(a => a.Id == t.AssetId && a.AssetTag == upper));
        }

        var result = await (
                from t in tickets
                join a in db.Set<Asset>() on t.AssetId equals a.Id
                orderby t.ReportedAt descending
                select new TicketListItem(
                    t.Id, t.Number, t.AssetId, a.AssetTag, a.Name, a.Criticality, t.Priority, t.Status, t.Issue, t.ReportedAt,
                    db.Set<User>().Where(u => u.Id == t.ReportedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                    t.AssignedTo, t.ResolvedAt, t.DowntimeMinutes))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> Ticket(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var row = await (
                from t in db.Set<MaintenanceTicket>().AsNoTracking()
                join a in db.Set<Asset>() on t.AssetId equals a.Id
                where t.Id == id
                select new
                {
                    Ticket = t, a.AssetTag, a.Name,
                    ReportedBy = db.Set<User>().Where(u => u.Id == t.ReportedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(MaintenanceTicket), id).ToProblem();
        }

        var ticket = row.Ticket;
        return TypedResults.Ok(new TicketDetail(
            ticket.Id, ticket.Number, ticket.AssetId, row.AssetTag, row.Name, ticket.Priority, ticket.Status, ticket.Issue, ticket.ReportedAt,
            row.ReportedBy, ticket.AssignedTo, ticket.AssignedToUserId, ticket.VendorId, ticket.AssignedAt, ticket.StartedAt, ticket.ResolvedAt,
            ticket.ClosedAt, ticket.Resolution, ticket.RootCause, ticket.Cost, ticket.DowntimeMinutes, ticket.Version));
    }

    private static async Task<IResult> Calibrations(Guid assetId, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<CalibrationRecord>().AsNoTracking()
            .Where(c => c.AssetId == assetId)
            .OrderByDescending(c => c.CalibratedOn)
            .Select(c => new CalibrationDto(c.Id, c.AssetId, c.CalibratedOn, c.Agency, c.CertificateNumber, c.CertificateFileId, c.NextDueDate, c.Outcome, c.Remarks))
            .ToListAsync(ct));
}
