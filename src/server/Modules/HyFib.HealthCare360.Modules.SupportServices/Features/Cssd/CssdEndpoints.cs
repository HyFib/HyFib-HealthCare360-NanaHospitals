using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;

/// <summary>/api/v1/cssd — instrument sets, department requests, soiled receipts, pack issues, recalls and the CSSD report (CSSD-01..05).</summary>
internal static class CssdEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/cssd").WithTags("CSSD");

        group.MapGet("/sets", ListSets).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdRequests);
        group.MapGet("/sets/{id:guid}", GetSet).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdRequests);
        group.MapPost("/sets", CreateSet).RequirePermission(SupportServicesPermissions.CssdMasters).WithValidation<SaveInstrumentSetRequest>();
        group.MapPut("/sets/{id:guid}", UpdateSet).RequirePermission(SupportServicesPermissions.CssdMasters).WithValidation<SaveInstrumentSetRequest>();

        group.MapGet("/requests", ListRequests).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdRequests);
        group.MapPost("/requests", async (CreateCssdRequestRequest r, CssdService cssd, CancellationToken ct) =>
                (await cssd.CreateRequestAsync(r, ct)).ToCreated(x => $"/api/v1/cssd/requests/{x.Id}"))
            .RequirePermission(SupportServicesPermissions.CssdRequests).WithValidation<CreateCssdRequestRequest>();
        group.MapPost("/requests/{id:guid}/cancel", async (Guid id, CancelCssdRequestRequest r, CssdService cssd, CancellationToken ct) =>
                (await cssd.CancelRequestAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(SupportServicesPermissions.CssdRequests).WithValidation<CancelCssdRequestRequest>();

        group.MapGet("/receipts", ListReceipts).RequirePermission(SupportServicesPermissions.CssdRead);
        group.MapPost("/receipts", async (ReceiveSoiledRequest r, CssdService cssd, CancellationToken ct) =>
                (await cssd.ReceiveSoiledAsync(r, ct)).ToCreated(x => $"/api/v1/cssd/receipts/{x.Id}"))
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<ReceiveSoiledRequest>();

        group.MapGet("/packs", ListPacks).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdProcess);

        group.MapGet("/issues", ListIssues).RequireAnyPermission(SupportServicesPermissions.CssdRead, SupportServicesPermissions.CssdRecalls);
        group.MapPost("/issues", async (IssuePacksRequest r, CssdService cssd, CancellationToken ct) => (await cssd.IssueAsync(r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CssdProcess).WithValidation<IssuePacksRequest>();
        group.MapPost("/issues/{id:guid}/use", async (Guid id, RecordPackUseRequest r, CssdService cssd, CancellationToken ct) =>
                (await cssd.RecordUseAsync(id, r, ct)).ToNoContent())
            .RequireAnyPermission(SupportServicesPermissions.CssdRequests, SupportServicesPermissions.CssdProcess).WithValidation<RecordPackUseRequest>();
        group.MapPost("/issues/{id:guid}/acknowledge-recall", async (Guid id, AcknowledgeRecallRequest r, CssdService cssd, CancellationToken ct) =>
                (await cssd.AcknowledgeRecallAsync(id, r.Action, ct)).ToNoContent())
            .RequirePermission(SupportServicesPermissions.CssdRecalls).WithValidation<AcknowledgeRecallRequest>();

        group.MapGet("/reports/summary", Summary).RequirePermission(SupportServicesPermissions.CssdRead);
    }

    private static async Task<IResult> ListSets(bool? includeInactive, string? search, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var sets = db.Set<InstrumentSet>().AsNoTracking().Include(s => s.Items).AsQueryable();
        if (includeInactive != true)
        {
            sets = sets.Where(s => s.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = search.Trim().ToLikePattern();
            sets = sets.Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Code, like));
        }

        var rows = await sets.OrderBy(s => s.Name).Take(500).ToListAsync(ct);
        var available = await AvailableCountsAsync(db, time.Today(), ct);
        return TypedResults.Ok(rows.Select(s => ToDto(s, available.GetValueOrDefault(s.Id))).ToList());
    }

    private static async Task<IResult> GetSet(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var set = await db.Set<InstrumentSet>().AsNoTracking().Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (set is null)
        {
            return Error.NotFound("Instrument set", id).ToProblem();
        }

        var available = await AvailableCountsAsync(db, time.Today(), ct);
        return TypedResults.Ok(ToDto(set, available.GetValueOrDefault(set.Id)));
    }

    private static async Task<IResult> CreateSet(SaveInstrumentSetRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<InstrumentSet>().AnyAsync(s => s.Code == code, ct))
        {
            return Error.Conflict("cssd.set_code_in_use", $"Set code {code} is already in use.").ToProblem();
        }

        var set = new InstrumentSet { FacilityId = facilityId };
        Apply(set, request, code);
        db.Add(set);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/cssd/sets/{set.Id}", ToDto(set, 0));
    }

    private static async Task<IResult> UpdateSet(Guid id, SaveInstrumentSetRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var set = await db.Set<InstrumentSet>().Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (set is null)
        {
            return Error.NotFound("Instrument set", id).ToProblem();
        }

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<InstrumentSet>().AnyAsync(s => s.Code == code && s.Id != id, ct))
        {
            return Error.Conflict("cssd.set_code_in_use", $"Set code {code} is already in use.").ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(set, version);
        }

        Apply(set, request, code);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListRequests(CssdRequestStatus? status, Guid? departmentId, bool? openOnly, CssdService cssd, HospitalDbContext db, CancellationToken ct)
    {
        var requests = db.Set<CssdRequest>().AsNoTracking();
        if (openOnly == true)
        {
            requests = requests.Where(r => r.Status == CssdRequestStatus.Open || r.Status == CssdRequestStatus.PartiallyIssued);
        }

        if (status is not null)
        {
            requests = requests.Where(r => r.Status == status);
        }

        if (departmentId is not null)
        {
            requests = requests.Where(r => r.DepartmentId == departmentId);
        }

        // Urgent first, then oldest first: the order CSSD should work the queue in.
        return TypedResults.Ok(await cssd.RequestDtosAsync(requests.OrderByDescending(r => r.Priority == CssdPriority.Urgent).ThenBy(r => r.CreatedAt).Take(200), ct));
    }

    private static async Task<IResult> ListReceipts(DateOnly? from, DateOnly? to, bool? discrepanciesOnly, CssdService cssd, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var (start, end) = Range(from, to, time);
        var receipts = db.Set<SoiledReceipt>().AsNoTracking().Where(r => r.ReceivedAt >= start && r.ReceivedAt < end);
        if (discrepanciesOnly == true)
        {
            receipts = receipts.Where(r => r.HasDiscrepancy);
        }

        return TypedResults.Ok(await cssd.ReceiptDtosAsync(receipts.OrderByDescending(r => r.ReceivedAt).Take(200), ct));
    }

    private static async Task<IResult> ListPacks(Guid? setId, PackStatus? status, int? expiringWithinDays, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var today = time.Today();
        var packs = db.Set<SterilePack>().AsNoTracking().Where(p => p.Status == (status ?? PackStatus.Available));
        if (setId is not null)
        {
            packs = packs.Where(p => p.InstrumentSetId == setId);
        }

        if (expiringWithinDays is { } days)
        {
            var limit = today.AddDays(Math.Clamp(days, 0, 365));
            packs = packs.Where(p => p.SterileUntil != null && p.SterileUntil <= limit);
        }

        var rows = await (
                from p in packs
                join l in db.Set<SterilizationLoad>() on p.LoadId equals l.Id
                join s in db.Set<InstrumentSet>() on p.InstrumentSetId equals s.Id
                join w in db.Set<WashCycle>() on p.WashCycleId equals (Guid?)w.Id into washes
                from w in washes.DefaultIfEmpty()
                orderby p.SterileUntil, p.PackNumber
                select new { Pack = p, l.CycleNumber, s.Name, WashNumber = w != null ? w.CycleNumber : null })
            .Take(500)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(r => new SterilePackDto(r.Pack.Id, r.Pack.PackNumber, r.Pack.LoadId, r.CycleNumber, r.Pack.InstrumentSetId, r.Name,
            r.Pack.Status, r.Pack.SterileUntil, r.Pack.IsExpired(today), r.Pack.IssuedToDepartmentId, r.Pack.WashCycleId, r.WashNumber)).ToList());
    }

    private static async Task<IResult> ListIssues(Guid? departmentId, Guid? loadId, bool? openRecalls, [AsParameters] PageQuery query, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var issues = db.Set<SetIssue>().AsNoTracking();
        if (departmentId is not null)
        {
            issues = issues.Where(i => i.DepartmentId == departmentId);
        }

        if (loadId is not null)
        {
            issues = issues.Where(i => i.LoadId == loadId);
        }

        if (openRecalls == true)
        {
            issues = issues.Where(i => i.RecallFlaggedAt != null && i.RecallAcknowledgedAt == null);
        }

        var total = await issues.LongCountAsync(ct);
        var items = await CssdService.IssueDtosAsync(db, issues.OrderByDescending(i => i.IssuedAt).Skip(page.Skip).Take(page.Take), ct);
        return TypedResults.Ok(new PagedResult<SetIssueDto>(items, page.Page, page.Take, total));
    }

    private static async Task<IResult> Summary(DateOnly? from, DateOnly? to, CssdService cssd, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber > 366)
        {
            return Error.Field("from", "Choose a period of up to one year.").ToProblem();
        }

        return TypedResults.Ok(await cssd.SummaryAsync(start, end, ct));
    }

    private static async Task<Dictionary<Guid, int>> AvailableCountsAsync(HospitalDbContext db, DateOnly today, CancellationToken ct) =>
        await db.Set<SterilePack>().AsNoTracking()
            .Where(p => p.Status == PackStatus.Available && (p.SterileUntil == null || p.SterileUntil >= today))
            .GroupBy(p => p.InstrumentSetId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

    private static (DateTimeOffset Start, DateTimeOffset End) Range(DateOnly? from, DateOnly? to, TimeProvider time)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-6);
        return (IndiaClock.StartOfDayUtc(start), IndiaClock.StartOfDayUtc(end.AddDays(1)));
    }

    private static void Apply(InstrumentSet set, SaveInstrumentSetRequest request, string code)
    {
        set.Code = code;
        set.Name = request.Name.Trim();
        set.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        set.OwnerDepartmentId = request.OwnerDepartmentId;
        set.Method = request.Method;
        set.ShelfLifeDays = request.ShelfLifeDays;
        set.IsActive = request.IsActive;
        set.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        set.ReplaceItems(request.Items.Select(i => new CountSheetLine(i.Name, i.Quantity)));
    }

    private static InstrumentSetDto ToDto(InstrumentSet s, int available) => new(
        s.Id, s.Code, s.Name, s.Category, s.OwnerDepartmentId, s.Method, s.ShelfLifeDays, s.IsActive, s.Notes,
        s.Items.OrderBy(i => i.SortOrder).Select(i => new InstrumentSetItemDto(i.Name, i.Quantity)).ToList(), s.InstrumentCount, available, s.Version);
}
