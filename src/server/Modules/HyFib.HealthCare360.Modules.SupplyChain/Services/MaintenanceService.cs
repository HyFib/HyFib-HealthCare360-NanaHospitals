using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Maintenance;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupplyChain.Services;

/// <summary>
/// Preventive maintenance (AST-03), breakdown tickets (AST-04) and calibration (AST-05). A breakdown that takes
/// equipment out of use sets the asset Under maintenance and alerts biomedical engineering at once; resolving
/// the last open ticket returns it to service.
/// </summary>
public sealed class MaintenanceService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>PM visits and calibrations are flagged this many days ahead (overdue ones always).</summary>
    public const int DueWindowDays = 7;

    /// <summary>Warranties and AMC/CMC contracts are flagged this many days before they lapse.</summary>
    public const int ExpiryWindowDays = 30;

    public const string PmKind = "PM";
    public const string CalibrationKind = "Calibration";
    public const string WarrantyKind = "Warranty";
    public const string ContractKind = "Contract";

    // ------------------------------------------------------------------ preventive maintenance

    public async Task<Result<Guid>> SavePlanAsync(Guid assetId, Guid? planId, SavePmPlanRequest request, CancellationToken ct)
    {
        if (!await db.Set<Asset>().AnyAsync(a => a.Id == assetId, ct))
        {
            return Error.NotFound(nameof(Asset), assetId);
        }

        PreventiveMaintenancePlan plan;
        if (planId is { } existingId)
        {
            var found = await db.Set<PreventiveMaintenancePlan>().FirstOrDefaultAsync(p => p.Id == existingId && p.AssetId == assetId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(PreventiveMaintenancePlan), existingId);
            }

            plan = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(plan, version);
            }
        }
        else
        {
            plan = new PreventiveMaintenancePlan { AssetId = assetId };
            db.Add(plan);
        }

        plan.Title = request.Title.Trim();
        plan.FrequencyDays = request.FrequencyDays;
        plan.NextDueDate = request.NextDueDate;
        plan.Checklist = request.Checklist?.Trim();
        plan.AssignedTo = request.AssignedTo?.Trim();
        plan.VendorId = request.VendorId;
        plan.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return plan.Id;
    }

    /// <summary>Records a PM visit; a completed visit moves the plan's next due date forward from the visit date.</summary>
    public async Task<Result<Guid>> RecordVisitAsync(Guid planId, RecordPmVisitRequest request, CancellationToken ct)
    {
        var plan = await db.Set<PreventiveMaintenancePlan>().FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return Error.NotFound(nameof(PreventiveMaintenancePlan), planId);
        }

        if (request.Outcome != PmVisitOutcome.NotDone)
        {
            var recorded = plan.RecordVisit(request.VisitDate);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        var visit = new PmVisit
        {
            PlanId = plan.Id,
            AssetId = plan.AssetId,
            VisitDate = request.VisitDate,
            PerformedBy = request.PerformedBy?.Trim(),
            VendorId = request.VendorId ?? plan.VendorId,
            Outcome = request.Outcome,
            Findings = request.Findings?.Trim(),
            Cost = request.Cost,
            NextDueDate = plan.NextDueDate,
            ServiceReportFileId = request.ServiceReportFileId,
        };
        db.Add(visit);
        await db.SaveChangesAsync(ct);
        return visit.Id;
    }

    // ------------------------------------------------------------------ breakdown tickets

    public async Task<Result<DocumentCreated>> ReportBreakdownAsync(ReportBreakdownRequest request, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().FirstOrDefaultAsync(a => a.Id == request.AssetId, ct);
        if (asset is null)
        {
            return Error.NotFound(nameof(Asset), request.AssetId);
        }

        if (!asset.IsInService)
        {
            return Error.Rule("asset.retired", $"Asset {asset.AssetTag} is {asset.Status}.");
        }

        // Failure of life-support equipment is never "low": default the priority from the asset's criticality.
        var priority = request.Priority ?? asset.Criticality switch
        {
            AssetCriticality.Critical => TicketPriority.Critical,
            AssetCriticality.High => TicketPriority.High,
            AssetCriticality.Medium => TicketPriority.Medium,
            _ => TicketPriority.Low,
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var ticket = new MaintenanceTicket
        {
            FacilityId = asset.FacilityId,
            Number = await numbers.NextAsync(SupplyChainNumberSeries.MaintenanceTicket, asset.FacilityId, ct),
            AssetId = asset.Id,
            ReportedByUserId = currentUser.UserId,
            ReportedAt = time.GetUtcNow(),
            Issue = request.Issue.Trim(),
            Priority = priority,
        };
        db.Add(ticket);
        if (request.AssetDown)
        {
            var down = asset.MarkUnderMaintenance();
            if (down.IsFailure)
            {
                return down.Error!;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(
            SupplyChainPermissions.AssetsMaintenance,
            asset.FacilityId,
            $"Breakdown {ticket.Number}: {asset.Name} ({asset.AssetTag})",
            $"{priority} priority. {ticket.Issue}",
            $"/assets/tickets/{ticket.Id}",
            "assets",
            priority >= TicketPriority.High ? "critical" : "warning",
            ct);
        return new DocumentCreated(ticket.Id, ticket.Number);
    }

    public Task<Result> AssignAsync(Guid ticketId, AssignTicketRequest request, CancellationToken ct) =>
        ChangeTicketAsync(ticketId, t => Task.FromResult(t.Assign(request.AssignedTo.Trim(), request.AssignedToUserId, request.VendorId, time.GetUtcNow())), ct);

    public Task<Result> StartAsync(Guid ticketId, CancellationToken ct) =>
        ChangeTicketAsync(ticketId, t => Task.FromResult(t.Start(time.GetUtcNow())), ct);

    public Task<Result> ResolveAsync(Guid ticketId, ResolveTicketRequest request, CancellationToken ct) =>
        ChangeTicketAsync(ticketId, async t =>
        {
            var resolved = t.Resolve(request.Resolution.Trim(), request.RootCause?.Trim(), request.Cost, request.DowntimeMinutes, time.GetUtcNow());
            if (resolved.IsFailure)
            {
                return resolved;
            }

            var otherOpen = await db.Set<MaintenanceTicket>().AnyAsync(
                o => o.AssetId == t.AssetId && o.Id != t.Id
                     && (o.Status == TicketStatus.Open || o.Status == TicketStatus.Assigned || o.Status == TicketStatus.InProgress), ct);
            var asset = await db.Set<Asset>().FirstAsync(a => a.Id == t.AssetId, ct);
            return !otherOpen && asset.Status == AssetStatus.UnderMaintenance ? asset.ReturnToService() : Result.Success();
        }, ct);

    public Task<Result> CloseAsync(Guid ticketId, CancellationToken ct) =>
        ChangeTicketAsync(ticketId, t => Task.FromResult(t.Close(time.GetUtcNow())), ct);

    public Task<Result> ReopenAsync(Guid ticketId, CancellationToken ct) =>
        ChangeTicketAsync(ticketId, t => Task.FromResult(t.Reopen()), ct);

    // ------------------------------------------------------------------ calibration

    /// <summary>Records a certificate; the asset's next calibration date follows the latest one, and a failed calibration takes the equipment out of use.</summary>
    public async Task<Result<Guid>> RecordCalibrationAsync(Guid assetId, RecordCalibrationRequest request, CancellationToken ct)
    {
        var asset = await db.Set<Asset>().FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset is null)
        {
            return Error.NotFound(nameof(Asset), assetId);
        }

        var record = new CalibrationRecord
        {
            AssetId = assetId,
            CalibratedOn = request.CalibratedOn,
            Agency = request.Agency.Trim(),
            CertificateNumber = request.CertificateNumber?.Trim(),
            CertificateFileId = request.CertificateFileId,
            NextDueDate = request.NextDueDate,
            Outcome = request.Outcome,
            Remarks = request.Remarks?.Trim(),
        };
        db.Add(record);

        var latest = await db.Set<CalibrationRecord>().Where(c => c.AssetId == assetId).MaxAsync(c => (DateOnly?)c.CalibratedOn, ct);
        if (latest is null || request.CalibratedOn >= latest)
        {
            asset.NextCalibrationDue = request.NextDueDate;
            asset.RequiresCalibration = true;
            if (request.Outcome == CalibrationResult.Fail && asset.IsInService)
            {
                var down = asset.MarkUnderMaintenance();
                if (down.IsFailure)
                {
                    return down.Error!;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return record.Id;
    }

    // ------------------------------------------------------------------ what falls due

    /// <summary>
    /// PM visits and calibrations due within <paramref name="dueWindowDays"/> (or overdue), warranties and AMC/CMC
    /// contracts lapsing within <paramref name="expiryWindowDays"/> (a renewed contract suppresses the old one).
    /// Runs without a signed-in facility from the reminder job, so every row carries its facility.
    /// </summary>
    public static async Task<List<MaintenanceDueItem>> DueItemsAsync(
        HospitalDbContext db, DateOnly today, int dueWindowDays, int expiryWindowDays, CancellationToken ct)
    {
        var dueLimit = today.AddDays(dueWindowDays);
        var expiryLimit = today.AddDays(expiryWindowDays);
        var inService = db.Set<Asset>().AsNoTracking().Where(a => a.Status != AssetStatus.Condemned && a.Status != AssetStatus.Disposed);

        var pm = await (
                from p in db.Set<PreventiveMaintenancePlan>().AsNoTracking()
                join a in inService on p.AssetId equals a.Id
                where p.IsActive && p.NextDueDate <= dueLimit
                select new { a.FacilityId, a.Id, a.AssetTag, a.Name, p.Title, Due = p.NextDueDate })
            .ToListAsync(ct);
        var calibration = await inService
            .Where(a => a.RequiresCalibration && a.NextCalibrationDue != null && a.NextCalibrationDue <= dueLimit)
            .Select(a => new { a.FacilityId, a.Id, a.AssetTag, a.Name, Due = a.NextCalibrationDue!.Value })
            .ToListAsync(ct);
        var warranty = await inService
            .Where(a => a.WarrantyExpiry != null && a.WarrantyExpiry >= today && a.WarrantyExpiry <= expiryLimit)
            .Select(a => new { a.FacilityId, a.Id, a.AssetTag, a.Name, Due = a.WarrantyExpiry!.Value })
            .ToListAsync(ct);
        var contracts = await (
                from c in db.Set<AssetContract>().AsNoTracking()
                join a in inService on c.AssetId equals a.Id
                where c.EndDate >= today && c.EndDate <= expiryLimit
                      && !db.Set<AssetContract>().Any(o => o.AssetId == c.AssetId && o.EndDate > c.EndDate)
                select new { a.FacilityId, a.Id, a.AssetTag, a.Name, c.Type, Due = c.EndDate })
            .ToListAsync(ct);

        return pm.Select(x => new MaintenanceDueItem(PmKind, x.FacilityId, x.Id, x.AssetTag, x.Name, x.Title, x.Due, x.Due.DayNumber - today.DayNumber))
            .Concat(calibration.Select(x => new MaintenanceDueItem(CalibrationKind, x.FacilityId, x.Id, x.AssetTag, x.Name, "Calibration due", x.Due, x.Due.DayNumber - today.DayNumber)))
            .Concat(warranty.Select(x => new MaintenanceDueItem(WarrantyKind, x.FacilityId, x.Id, x.AssetTag, x.Name, "Warranty expires", x.Due, x.Due.DayNumber - today.DayNumber)))
            .Concat(contracts.Select(x => new MaintenanceDueItem(ContractKind, x.FacilityId, x.Id, x.AssetTag, x.Name, $"{x.Type.ToString().ToUpperInvariant()} expires", x.Due, x.Due.DayNumber - today.DayNumber)))
            .OrderBy(x => x.DueDate)
            .ThenBy(x => x.AssetName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<Result> ChangeTicketAsync(Guid ticketId, Func<MaintenanceTicket, Task<Result>> change, CancellationToken ct)
    {
        var ticket = await db.Set<MaintenanceTicket>().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return Error.NotFound(nameof(MaintenanceTicket), ticketId);
        }

        var result = await change(ticket);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
