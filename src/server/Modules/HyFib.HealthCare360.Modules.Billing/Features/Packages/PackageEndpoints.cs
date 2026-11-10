using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Packages;

/// <summary>
/// /api/v1/billing/packages — fixed-price treatment packages with inclusions and caps (MB-02, IPB-04), for admissions,
/// out-patient visits or both. Whoever prepares OP bills reads them to choose a bill's package.
/// </summary>
internal static class PackageEndpoints
{
    private const string WaitingCode = "approval.already_requested";

    private static readonly string[] Readers =
        [BillingPermissions.TariffsManage, BillingPermissions.IpManage, BillingPermissions.ChargesRead, BillingPermissions.OpCreate];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/packages").WithTags("Billing - Packages");
        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/", Create).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SavePackageRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SavePackageRequest>().RequireVersion();
    }

    private static async Task<IResult> List(bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var packages = db.Set<Package>().AsNoTracking();
        if (includeInactive != true)
        {
            packages = packages.Where(p => p.IsActive);
        }

        var rows = await packages.OrderBy(p => p.Name)
            .Select(p => new PackageListItem(p.Id, p.Code, p.Name, p.Price, p.LengthOfStayDays, p.TariffPlanId, p.UsedFor, p.IsActive, p.Inclusions.Count, p.HbpCode))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PackageApprovals approvals, CancellationToken ct)
    {
        var package = await db.Set<Package>().AsNoTracking().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct);
        return package is null
            ? Error.NotFound(nameof(Package), id).ToProblem()
            : TypedResults.Ok(await ToDetailAsync(db, package, ct) with { PendingTerms = await approvals.PendingAsync(package.Id, ct) });
    }

    private static async Task<IResult> Create(SavePackageRequest request, HospitalDbContext db, HbpService hbp, PackageApprovals approvals, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Package>().IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
        {
            return Error.Conflict("billing.package_code_exists", $"A package with code {code} already exists.").ToProblem();
        }

        var package = new Package { Code = code };
        var checkedRequest = await CheckAsync(db, hbp, package, request, ct);
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!.ToProblem();
        }

        // What a package's patients pay changes only when two people agree (maker-checker): a new package is made out of
        // use, at ₹0 and covering nothing, and goes live with the terms a second person approves.
        ApplyDetails(package, request, checkedRequest.Value);
        var asked = Terms(request);
        var now = asked.OutOfUse();
        now.ApplyTo(package);
        db.Add(package);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        PendingPackageTerms? pending = null;
        if (asked != now)
        {
            var approval = await approvals.AskAsync(package, now, asked, request.Reason, ct);
            if (approval.IsFailure)
            {
                return approval.Error!.ToProblem();
            }

            // As GET reads it: with the service it would be billed as and its covers named.
            pending = await approvals.PendingAsync(package.Id, ct);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/billing/packages/{package.Id}", await ToDetailAsync(db, package, ct) with { PendingTerms = pending });
    }

    private static async Task<IResult> Update(Guid id, SavePackageRequest request, HospitalDbContext db, HbpService hbp, PackageApprovals approvals, CancellationToken ct)
    {
        var package = await db.Set<Package>().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (package is null)
        {
            return Error.NotFound(nameof(Package), id).ToProblem();
        }

        db.GuardVersion(package, request.Version ?? 0);
        var checkedRequest = await CheckAsync(db, hbp, package, request, ct);
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!.ToProblem();
        }

        // The terms wait for a second person, except taking the package out of use, which only stops it being chosen
        // again. While a change waits, a save must ask for the same terms.
        var before = PackageTerms.Of(package);
        var asked = Terms(request);
        var waiting = await approvals.PendingAsync(package.Id, ct);
        if (waiting is not null && waiting.Asked != asked)
        {
            return Error.Conflict(WaitingCode,
                    $"A change to package {package.Code}'s price or cover, asked for by {waiting.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.")
                .ToProblem();
        }

        ApplyDetails(package, request, checkedRequest.Value);
        var now = before.Narrowed(asked);
        if (asked == now && asked.NotesDiffer(now))
        {
            // Only the covers' notes changed: they decide nothing, and are saved at once.
            asked.ApplyTo(package);
        }
        else
        {
            package.IsActive = now.IsActive;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (waiting is null && asked != now)
        {
            var approval = await approvals.AskAsync(package, now, asked, request.Reason, ct);
            if (approval.IsFailure)
            {
                return approval.Error!.ToProblem();
            }

            await transaction.CommitAsync(ct);
            return TypedResults.Accepted($"/api/v1/approvals/{approval.Value.Id}", approval.Value);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await ToDetailAsync(db, package, ct) with { PendingTerms = waiting });
    }

    /// <summary>Checks a save: its PM-JAY procedure, the service it is billed as, and the services it covers. The procedure code to keep.</summary>
    private static async Task<Result<string?>> CheckAsync(HospitalDbContext db, HbpService hbp, Package package, SavePackageRequest request, CancellationToken ct)
    {
        var hbpCode = await hbp.CheckForPackageAsync(request.HbpCode, package.HbpCode, ct);
        if (hbpCode.IsFailure)
        {
            return hbpCode.Error!;
        }

        var item = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.ServiceItemId, ct);
        if (item is null || item.Category != ServiceCategory.Package)
        {
            return Error.Field("serviceItemId", "Choose a catalogue service of category Package to bill the package under.");
        }

        var includedIds = request.Inclusions.Where(i => i.ServiceItemId is not null).Select(i => i.ServiceItemId!.Value).Distinct().ToList();
        if (includedIds.Count > 0 && await db.Set<ServiceItem>().CountAsync(s => includedIds.Contains(s.Id), ct) != includedIds.Count)
        {
            return Error.Field("inclusions", "One or more included services were not found.");
        }

        return hbpCode.Value;
    }

    /// <summary>The details a save makes at once: the name, stay, description and the PM-JAY procedure it is claimed as.</summary>
    private static void ApplyDetails(Package package, SavePackageRequest request, string? hbpCode)
    {
        package.Name = request.Name.Trim();
        package.LengthOfStayDays = request.LengthOfStayDays;
        package.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        package.HbpCode = hbpCode;
    }

    /// <summary>The terms a save asks for: what decides what the package's patients pay.</summary>
    private static PackageTerms Terms(SavePackageRequest request) => PackageTerms.Create(
        request.ServiceItemId, request.Price, request.TariffPlanId, request.UsedFor, request.IsActive,
        request.Inclusions.Select(i => new PackageCover(i.ServiceItemId, i.ServiceItemId is null ? i.Category : null, i.MaxQuantity, i.MaxAmount,
            string.IsNullOrWhiteSpace(i.Notes) ? null : i.Notes.Trim())));

    private static async Task<PackageDetail> ToDetailAsync(HospitalDbContext db, Package package, CancellationToken ct)
    {
        var ids = package.Inclusions.Where(i => i.ServiceItemId is not null).Select(i => i.ServiceItemId!.Value).Append(package.ServiceItemId).Distinct().ToList();
        var items = await db.Set<ServiceItem>().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => new { s.Code, s.Name }, ct);
        var hbpName = package.HbpCode is { } hbpCode
            ? await db.Set<HbpProcedure>().Where(p => p.Code == hbpCode).Select(p => p.ProcedureName).FirstOrDefaultAsync(ct)
            : null;
        return new PackageDetail(
            package.Id, package.Code, package.Name, package.ServiceItemId, items.GetValueOrDefault(package.ServiceItemId)?.Code, package.Price,
            package.LengthOfStayDays, package.TariffPlanId, package.UsedFor, package.Description, package.IsActive,
            package.Inclusions.Select(i => new PackageInclusionDto(
                i.Id, i.ServiceItemId, i.ServiceItemId is { } sid ? items.GetValueOrDefault(sid)?.Code : null,
                i.ServiceItemId is { } nid ? items.GetValueOrDefault(nid)?.Name : null, i.Category, i.MaxQuantity, i.MaxAmount, i.Notes)).ToList(),
            package.Version,
            package.HbpCode,
            hbpName);
    }
}
