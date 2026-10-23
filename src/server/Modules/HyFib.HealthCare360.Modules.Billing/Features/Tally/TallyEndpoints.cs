using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tally;

/// <summary>
/// /api/v1/billing/tally (INT-TALLY): the posting log, (re)queueing, the voucher preview and the manual import file; per
/// day, the vouchers with Tally's answers, the reconciliation checks and "mark imported"; and the ledger names the day
/// book posts to. Everything works on the signed-in user's facility.
/// </summary>
internal static class TallyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/tally").WithTags("Billing - Tally");
        group.MapGet("/exports", List).RequirePermission(BillingPermissions.TallyExport);
        group.MapPost("/exports", Queue).RequirePermission(BillingPermissions.TallyExport).WithValidation<PostDayBookRequest>();
        group.MapGet("/day-book", Vouchers).RequirePermission(BillingPermissions.TallyExport);
        group.MapGet("/export-file", ExportFile).RequirePermission(BillingPermissions.TallyExport);
        group.MapGet("/days/{date}/vouchers", DayVouchers).RequirePermission(BillingPermissions.TallyExport);
        group.MapGet("/days/{date}/check", Preview).RequirePermission(BillingPermissions.TallyExport);
        group.MapPost("/days/{date}/check", Check).RequirePermission(BillingPermissions.TallyExport);
        group.MapPost("/days/{date}/imported", MarkImported).RequirePermission(BillingPermissions.TallyExport);
        group.MapGet("/ledgers", Ledgers).RequireAnyPermission(BillingPermissions.TallyExport, BillingPermissions.TallyConfigure);
        group.MapPut("/ledgers", SaveLedgers).RequirePermission(BillingPermissions.TallyConfigure).WithValidation<TallyLedgerMap>().FirstSaveCreates();
    }

    private static async Task<IResult> List(DateOnly? from, DateOnly? to, HospitalDbContext db, ICurrentUser currentUser, TimeProvider time, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var end = to ?? BillingTime.Today(time);
        var start = from ?? end.AddDays(-30);
        if (end.DayNumber - start.DayNumber > 366)
        {
            return Error.Field("from", "Show at most a year at a time.").ToProblem();
        }

        var rows = await db.Set<TallyExport>().AsNoTracking()
            .Where(e => e.FacilityId == facilityId && e.Date >= start && e.Date <= end)
            .OrderByDescending(e => e.Date)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(TallyExportDto.From).ToList());
    }

    private static async Task<IResult> Queue(PostDayBookRequest request, TallyService tally, ICurrentUser currentUser, CancellationToken ct) =>
        currentUser.FacilityId is { } facilityId
            ? (await tally.QueueAsync(facilityId, request.Date, request.To ?? request.Date, ct)).ToNoContent()
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Vouchers(DateOnly date, TallyService tally, ICurrentUser currentUser, CancellationToken ct) =>
        currentUser.FacilityId is { } facilityId
            ? TypedResults.Ok(await tally.BuildVouchersAsync(facilityId, date, ct))
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> ExportFile(DateOnly date, TallyService tally, ICurrentUser currentUser, IAuditLogger audit, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var file = await tally.ExportFileAsync(facilityId, date, ct);
        if (file.IsFailure)
        {
            return file.Error!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.Data, "billing.tally.exported", $"Downloaded the Tally day book of {date:yyyy-MM-dd}"), ct);
        return TypedResults.File(file.Value.Content, file.Value.ContentType, file.Value.FileName);
    }

    private static async Task<IResult> DayVouchers(DateOnly date, TallyService tally, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var (export, records) = await tally.VoucherRecordsAsync(facilityId, date, ct);
        return TypedResults.Ok(new TallyDayVouchersDto(date, export?.Status, TallyTracking.Count(records), records.Select(TallyVoucherDto.From).ToList()));
    }

    private static async Task<IResult> Preview(DateOnly date, TallyReconciliationService reconciliation, ICurrentUser currentUser, CancellationToken ct) =>
        currentUser.FacilityId is { } facilityId
            ? TypedResults.Ok(await reconciliation.PreviewAsync(facilityId, date, ct))
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Check(DateOnly date, bool? againstTally, TallyReconciliationService reconciliation, ICurrentUser currentUser, IAuditLogger audit, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var check = await reconciliation.CheckAsync(facilityId, date, againstTally ?? false, ct);
        if (againstTally == true)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Integration, "billing.tally.checked", $"Checked the Tally day book of {date:yyyy-MM-dd} against Tally"), ct);
        }

        return TypedResults.Ok(check);
    }

    private static async Task<IResult> MarkImported(
        DateOnly date, TallyService tally, TallyReconciliationService reconciliation, ICurrentUser currentUser, IAuditLogger audit, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var marked = await tally.MarkImportedAsync(facilityId, date, currentUser.UserId, currentUser.DisplayName ?? currentUser.UserName, ct);
        if (marked.IsFailure)
        {
            return marked.Error!.ToProblem();
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.Data, "billing.tally.imported", $"Marked the Tally day book of {date:yyyy-MM-dd} imported into Tally")
        {
            EntityType = nameof(TallyExport),
            EntityId = marked.Value.Id.ToString(),
            Details = new { marked.Value.VoucherCount },
        }, ct);

        // The day's records check now compares the day book with what was imported.
        await reconciliation.CheckAsync(facilityId, date, againstTally: false, ct);
        return TypedResults.Ok(TallyExportDto.From(marked.Value));
    }

    /// <summary>The ledger names in effect for the facility, built-in names filled in.</summary>
    private static async Task<IResult> Ledgers(TallyService tally, HospitalDbContext db, ICurrentUser currentUser, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var map = await tally.LedgersAsync(facilityId, ct);
        return TypedResults.Ok(map with
        {
            Pharmacy = map.CounterLedgers(TallyCounters.Pharmacy),
            Canteen = map.CounterLedgers(TallyCounters.Canteen),
            Version = await SettingVersions.OfAsync(db, TallyService.LedgerSetting, facilityId, ct),
        });
    }

    /// <summary>Saves the ledger names for the signed-in facility (they override the hospital-wide names).</summary>
    private static async Task<IResult> SaveLedgers(TallyLedgerMap request, ISettingsService settings, ICurrentUser currentUser, IAuditLogger audit, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        await settings.SetAsync(TallyService.LedgerSetting, TallyLedgerMaps.Normalize(request) with { Version = null }, facilityId, false, request.Version, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Admin, "billing.tally.ledgers", "Changed the Tally ledger names")
        {
            EntityType = "Setting",
            EntityId = TallyService.LedgerSetting,
            Details = request,
        }, ct);
        return TypedResults.NoContent();
    }
}
