using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Sterilizer cycles (CSSD-04) and recall by load (CSSD-05): loads are numbered "{FAC}CS{YY}{SEQ:6}", each pack gets a
/// label with the cycle number, and a biological-indicator failure after release flags every issue of the load, pulls
/// its packs from the shelf and alerts CSSD, OT and quality staff in the same transaction.
/// </summary>
public sealed class SterilizationService(
    HospitalDbContext db,
    INumberSeries numbers,
    INotificationService notifications,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string CycleSeries = "cssd-cycle";
    public const string CycleFormat = "{FAC}CS{YY}{SEQ:6}";

    public async Task<Result<LoadDto>> CreateLoadAsync(CreateLoadRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var setIds = request.Packs.Select(p => p.InstrumentSetId).Distinct().ToList();
        var sets = await db.Set<InstrumentSet>().AsNoTracking().Where(s => setIds.Contains(s.Id) && s.IsActive).ToDictionaryAsync(s => s.Id, ct);
        var missing = setIds.FirstOrDefault(id => !sets.ContainsKey(id));
        if (missing != Guid.Empty)
        {
            return Error.Field("packs", "Every set in the load must be an active instrument set.");
        }

        var wrongMethod = sets.Values.FirstOrDefault(s => s.Method != request.Method);
        if (wrongMethod is not null)
        {
            return Error.Rule("cssd.method_mismatch", $"{wrongMethod.Name} is processed by {wrongMethod.Method}; this load is {request.Method}.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var load = new SterilizationLoad
        {
            FacilityId = facilityId,
            CycleNumber = await numbers.NextAsync(CycleSeries, facilityId, CycleFormat, NumberResetPolicy.Yearly, ct),
            SterilizerName = request.SterilizerName.Trim(),
            Method = request.Method,
            Program = Clean(request.Program),
            ContainsImplants = request.ContainsImplants,
            LoadedAt = time.GetUtcNow(),
            OperatorName = Clean(request.OperatorName) ?? currentUser.DisplayName,
            OperatorUserId = currentUser.UserId,
            Status = LoadStatus.Loaded,
            Notes = Clean(request.Notes),
        };

        var sequence = 0;
        foreach (var line in request.Packs)
        {
            for (var i = 0; i < line.Quantity; i++)
            {
                sequence++;
                load.Packs.Add(new SterilePack
                {
                    FacilityId = facilityId,
                    LoadId = load.Id,
                    InstrumentSetId = line.InstrumentSetId,
                    PackNumber = $"{load.CycleNumber}-{sequence:000}",
                    ShelfLifeDays = sets[line.InstrumentSetId].ShelfLifeDays,
                    Status = PackStatus.InProcess,
                });
            }
        }

        db.Add(load);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ToDtoAsync(load, ct);
    }

    public Task<Result<LoadDto>> StartAsync(Guid loadId, CancellationToken ct) =>
        ChangeAsync(loadId, (load, now) => Task.FromResult(load.Start(now)), ct);

    public Task<Result<LoadDto>> CompleteAsync(Guid loadId, CompleteLoadRequest request, CancellationToken ct) =>
        ChangeAsync(loadId, (load, now) =>
            Task.FromResult(load.Complete(request.TemperatureC, request.PressureKpa, request.ExposureMinutes, request.ChemicalIndicator, now)), ct);

    public Task<Result<LoadDto>> ReleaseAsync(Guid loadId, CancellationToken ct) =>
        ChangeAsync(loadId, (load, now) => Task.FromResult(load.Release(currentUser.UserId, currentUser.DisplayName, time.Today(), now)), ct);

    public Task<Result<LoadDto>> AbortAsync(Guid loadId, string reason, CancellationToken ct) =>
        ChangeAsync(loadId, (load, now) => Task.FromResult(load.Abort(reason, now)), ct);

    /// <summary>Records the biological indicator; a failure on a released load triggers the recall in the same transaction.</summary>
    public async Task<Result<LoadDto>> RecordBiologicalIndicatorAsync(Guid loadId, BiologicalIndicatorRequest request, CancellationToken ct)
    {
        RecallPlan plan = RecallPlan.None;
        var result = await ChangeAsync(loadId, async (load, now) =>
        {
            var recorded = load.RecordBiologicalIndicator(request.Result, request.Lot, now);
            if (recorded.IsSuccess && load.RequiresRecall)
            {
                plan = await ApplyRecallAsync(load, now, ct);
            }

            return recorded;
        }, ct);

        if (result.IsSuccess && !plan.IsEmpty)
        {
            await notifications.NotifyPermissionHoldersAsync(
                SupportServicesPermissions.CssdRecalls,
                currentUser.FacilityId,
                $"CSSD recall: load {result.Value.CycleNumber}",
                $"Biological indicator failed. {plan.IssuesToFlag.Count} issued pack(s) flagged, {plan.PacksToRecall.Count} pack(s) pulled back; "
                + $"{plan.PatientsExposed.Count} patient(s) may be affected. Acknowledge each pack in CSSD recalls.",
                $"/cssd/loads/{loadId}",
                "cssd",
                "critical",
                ct);
        }

        return result;
    }

    public async Task<Result<RecallReport>> RecallReportAsync(Guid loadId, CancellationToken ct)
    {
        var load = await db.Set<SterilizationLoad>().AsNoTracking().Include(l => l.Packs).FirstOrDefaultAsync(l => l.Id == loadId, ct);
        if (load is null)
        {
            return Error.NotFound("Sterilization load", loadId);
        }

        var rows = await (
                from i in db.Set<SetIssue>().AsNoTracking()
                join s in db.Set<InstrumentSet>() on i.InstrumentSetId equals s.Id
                join pk in db.Set<SterilePack>() on i.PackId equals pk.Id
                join d in db.Set<Department>() on i.DepartmentId equals d.Id into departments
                from d in departments.DefaultIfEmpty()
                join p in db.Set<Patient>() on i.PatientId equals p.Id into patients
                from p in patients.DefaultIfEmpty()
                where i.LoadId == loadId
                orderby i.IssuedAt
                select new RecallItem(i.Id, pk.PackNumber, s.Name, i.DepartmentId, d != null ? d.Name : null, i.IssuedAt, i.Status, i.PatientId,
                    p != null ? p.Uhid : null, p != null ? p.FullName : null, i.RecallFlaggedAt, i.RecallAcknowledgedAt, i.RecallAction))
            .ToListAsync(ct);

        return new RecallReport(load.Id, load.CycleNumber, load.RequiresRecall, load.Packs.Count(p => p.Status == PackStatus.Recalled),
            rows.Count(r => r.RecallFlaggedAt is not null), rows.Count(r => r.RecallAcknowledgedAt is not null),
            rows.Where(r => r.PatientId is not null).Select(r => r.PatientId).Distinct().Count(), rows);
    }

    public async Task<Result<LoadDto>> GetAsync(Guid loadId, CancellationToken ct)
    {
        var load = await db.Set<SterilizationLoad>().AsNoTracking().Include(l => l.Packs).FirstOrDefaultAsync(l => l.Id == loadId, ct);
        return load is null ? Error.NotFound("Sterilization load", loadId) : await ToDtoAsync(load, ct);
    }

    private async Task<RecallPlan> ApplyRecallAsync(SterilizationLoad load, DateTimeOffset now, CancellationToken ct)
    {
        var issues = await db.Set<SetIssue>().Where(i => i.LoadId == load.Id).ToListAsync(ct);
        var plan = CssdRecall.Plan(load.BiologicalIndicator, load.ReleasedAt is not null,
            load.Packs.Select(p => new PackSnapshot(p.Id, p.Status)).ToList(),
            issues.Select(i => new IssueSnapshot(i.Id, i.PackId, i.Status, i.PatientId, i.RecallFlaggedAt is not null)).ToList());
        if (plan.IsEmpty)
        {
            return plan;
        }

        var reason = $"Load {load.CycleNumber}: {load.FailureReason}";
        foreach (var pack in load.Packs.Where(p => plan.PacksToRecall.Contains(p.Id)))
        {
            pack.Status = PackStatus.Recalled;
        }

        foreach (var issue in issues.Where(i => plan.IssuesToFlag.Contains(i.Id)))
        {
            issue.FlagRecall(reason, now);
        }

        audit.Add(new AuditEntry(AuditCategories.Clinical, "cssd.recall", $"Recall of load {load.CycleNumber}: {load.FailureReason}")
        {
            EntityType = nameof(SterilizationLoad),
            EntityId = load.Id.ToString(),
            Severity = AuditSeverity.Critical,
            Details = new { issues = plan.IssuesToFlag.Count, packs = plan.PacksToRecall.Count, patients = plan.PatientsExposed.Count },
        });
        return plan;
    }

    private async Task<Result<LoadDto>> ChangeAsync(Guid loadId, Func<SterilizationLoad, DateTimeOffset, Task<Result>> change, CancellationToken ct)
    {
        var load = await db.Set<SterilizationLoad>().Include(l => l.Packs).FirstOrDefaultAsync(l => l.Id == loadId, ct);
        if (load is null)
        {
            return Error.NotFound("Sterilization load", loadId);
        }

        var result = await change(load, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(load, ct);
    }

    private async Task<LoadDto> ToDtoAsync(SterilizationLoad load, CancellationToken ct)
    {
        var setIds = load.Packs.Select(p => p.InstrumentSetId).Distinct().ToList();
        var setNames = await db.Set<InstrumentSet>().AsNoTracking().Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var today = time.Today();
        return new LoadDto(
            load.Id, load.CycleNumber, load.SterilizerName, load.Method, load.Program, load.ContainsImplants, load.Status, load.LoadedAt, load.StartedAt,
            load.CompletedAt, load.TemperatureC, load.PressureKpa, load.ExposureMinutes, load.ChemicalIndicator, load.BiologicalIndicator,
            load.BiologicalIndicatorLot, load.BiologicalReadAt, load.OperatorName, load.ReleasedAt, load.ReleasedByName, load.FailureReason, load.Notes,
            load.Packs.OrderBy(p => p.PackNumber)
                .Select(p => new SterilePackDto(p.Id, p.PackNumber, p.LoadId, load.CycleNumber, p.InstrumentSetId, setNames.GetValueOrDefault(p.InstrumentSetId),
                    p.Status, p.SterileUntil, p.IsExpired(today), p.IssuedToDepartmentId))
                .ToList(),
            load.Version);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
