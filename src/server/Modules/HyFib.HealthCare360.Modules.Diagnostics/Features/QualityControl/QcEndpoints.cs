using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;

/// <summary>
/// /api/v1/lab/qc: control lots, QC runs judged by the Westgard rules, reviews with corrective action, Levey-Jennings
/// data, today's QC board and audited lockout overrides (LIS-06).
/// </summary>
internal static class QcEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var qc = app.MapGroup("/api/v1/lab/qc").WithTags("Laboratory QC");
        qc.MapGet("/lots", ListLots).RequireAnyPermission(DiagnosticsPermissions.LabQcManage, DiagnosticsPermissions.LabResultsVerify);
        qc.MapPost("/lots", (SaveQcLotRequest request, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) => SaveLot(null, request, db, user, time, ct))
            .RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<SaveQcLotRequest>();
        qc.MapPut("/lots/{id:guid}", (Guid id, SaveQcLotRequest request, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct) => SaveLot(id, request, db, user, time, ct))
            .RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<SaveQcLotRequest>();
        qc.MapGet("/lots/{id:guid}/levey-jennings", LeveyJennings).RequireAnyPermission(DiagnosticsPermissions.LabQcManage, DiagnosticsPermissions.LabResultsVerify);
        qc.MapGet("/runs", ListRuns).RequireAnyPermission(DiagnosticsPermissions.LabQcManage, DiagnosticsPermissions.LabResultsVerify);
        qc.MapPost("/runs", RecordRun).RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<RecordQcRunRequest>();
        qc.MapPost("/runs/{id:guid}/review", Review).RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<ReviewQcRunRequest>();
        qc.MapGet("/status", Status)
            .RequireAnyPermission(DiagnosticsPermissions.LabQcManage, DiagnosticsPermissions.LabResultsVerify, DiagnosticsPermissions.LabResultsEnter);
        qc.MapPost("/overrides", Override).RequirePermission(DiagnosticsPermissions.LabResultsVerify).WithValidation<QcOverrideRequest>();
    }

    private static async Task<IResult> ListLots(bool? includeInactive, Guid? labTestId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = DiagnosticsTime.Today(time);
        var lots = await (
                from l in db.Set<QcLot>().AsNoTracking()
                join t in db.Set<LabTest>() on l.LabTestId equals t.Id
                where (includeInactive == true || l.IsActive) && (labTestId == null || l.LabTestId == labTestId)
                orderby t.Name, l.Level, l.ExpiryDate
                select new { l, t.Code, t.Name })
            .ToListAsync(ct);
        return TypedResults.Ok(lots.Select(x => new QcLotDto(
            x.l.Id, x.l.LabTestId, x.Code, x.Name, x.l.Material, x.l.Level, x.l.LotNumber, x.l.ExpiryDate, x.l.TargetMean, x.l.TargetSd, x.l.Unit,
            x.l.InstrumentCode, x.l.IsActive, x.l.IsExpiredOn(today), x.l.Version)).ToList());
    }

    private static async Task<IResult> SaveLot(Guid? id, SaveQcLotRequest request, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        var test = await db.Set<LabTest>().AsNoTracking().Where(t => t.Id == request.LabTestId).Select(t => new { t.IsPanel, t.ResultType }).FirstOrDefaultAsync(ct);
        if (test is null || test.IsPanel || test.ResultType != LabResultType.Numeric)
        {
            return Error.Field("labTestId", "Quality control is run for a numeric analyte, not a panel.").ToProblem();
        }

        QcLot lot;
        if (id is { } existingId)
        {
            var found = await db.Set<QcLot>().FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(QcLot), existingId).ToProblem();
            }

            lot = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(lot, version);
            }

            // Changing targets would silently re-judge the history plotted on the Levey-Jennings chart.
            if (lot.TargetMean != request.TargetMean || lot.TargetSd != request.TargetSd)
            {
                if (await db.Set<QcRun>().AnyAsync(r => r.QcLotId == lot.Id, ct))
                {
                    return Error.Rule("qc.lot_in_use", "This lot already has runs; register a new lot to change the target mean or SD.").ToProblem();
                }
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            if (request.ExpiryDate < DiagnosticsTime.Today(time))
            {
                return Error.Field("expiryDate", "The control lot has already expired.").ToProblem();
            }

            lot = new QcLot { FacilityId = facilityId };
            db.Add(lot);
        }

        lot.LabTestId = request.LabTestId;
        lot.Material = request.Material.Trim();
        lot.Level = request.Level;
        lot.LotNumber = request.LotNumber.Trim();
        lot.ExpiryDate = request.ExpiryDate;
        lot.TargetMean = request.TargetMean;
        lot.TargetSd = request.TargetSd;
        lot.Unit = string.IsNullOrWhiteSpace(request.Unit) ? null : request.Unit.Trim();
        lot.InstrumentCode = string.IsNullOrWhiteSpace(request.InstrumentCode) ? null : request.InstrumentCode.Trim().ToUpperInvariant();
        lot.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/lab/qc/lots/{lot.Id}", new { lot.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> LeveyJennings(Guid id, DateOnly? from, DateOnly? to, QualityControlService qc, CancellationToken ct) =>
        (await qc.LeveyJenningsAsync(id, from, to, ct)).ToOk();

    private static async Task<IResult> ListRuns(
        [AsParameters] PageQuery query, Guid? labTestId, Guid? lotId, QcOutcome? outcome, DateOnly? from, DateOnly? to, HospitalDbContext db,
        DiagnosticsLookups lookups, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var (start, end) = DiagnosticsTime.Range(from, to, time, maxDays: 366);
        var runs = db.Set<QcRun>().AsNoTracking().Where(r => r.RunAt >= start && r.RunAt < end);
        if (labTestId is not null)
        {
            runs = runs.Where(r => r.LabTestId == labTestId);
        }

        if (lotId is not null)
        {
            runs = runs.Where(r => r.QcLotId == lotId);
        }

        if (outcome is not null)
        {
            runs = runs.Where(r => r.Outcome == outcome);
        }

        var paged = await runs.OrderByDescending(r => r.RunAt).ToPagedResultAsync(page, ct);
        var lotIds = paged.Items.Select(r => r.QcLotId).Distinct().ToList();
        var runIds = paged.Items.Select(r => r.Id).ToList();
        var lots = await db.Set<QcLot>().AsNoTracking().Where(l => lotIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var overridden = await db.Set<QcOverride>().AsNoTracking().Where(o => runIds.Contains(o.QcRunId)).Select(o => o.QcRunId).ToListAsync(ct);
        var users = await lookups.UserNamesAsync(paged.Items.SelectMany(r => new[] { r.CreatedBy, r.ReviewedBy }), ct);
        return TypedResults.Ok(paged.Map(r => QualityControlService.ToDto(r, lots[r.QcLotId], users, overridden.Contains(r.Id))));
    }

    private static async Task<IResult> RecordRun(RecordQcRunRequest request, QualityControlService qc, CancellationToken ct) =>
        (await qc.RecordRunAsync(request, ct)).ToCreated(run => $"/api/v1/lab/qc/runs/{run.Id}");

    private static async Task<IResult> Review(Guid id, ReviewQcRunRequest request, QualityControlService qc, CancellationToken ct) =>
        (await qc.ReviewAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Status(QualityControlService qc, ICurrentUser user, CancellationToken ct) =>
        TypedResults.Ok(await qc.StatusAsync(user.FacilityId, ct));

    private static async Task<IResult> Override(QcOverrideRequest request, QualityControlService qc, CancellationToken ct) =>
        (await qc.OverrideAsync(request, ct)).ToHttpResult(count => TypedResults.Ok(new { overrides = count }));
}
