using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Packages;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// What a package absorbs: one service or a whole category, up to a quantity and an amount. The notes say why, and decide
/// nothing.
/// </summary>
public sealed record PackageCover(Guid? ServiceItemId, ServiceCategory? Category, decimal? MaxQuantity, decimal? MaxAmount, string? Notes)
{
    /// <summary>Whether two covers absorb the same, whatever their notes say.</summary>
    public bool SameRule(PackageCover other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ServiceItemId == other.ServiceItemId && Category == other.Category && MaxQuantity == other.MaxQuantity && MaxAmount == other.MaxAmount;
    }
}

/// <summary>
/// What a package decides about what its patients pay (maker-checker): the catalogue service it is billed as (whose GST
/// rate the package line carries), its price, the plan it is priced for (none: any plan), the bills it may price, whether
/// it is in use, and what it covers. An admission's package is priced at its final bill from the package as it is then,
/// so a change reaches every stay already on it. Taking a package out of use only stops it being chosen again, so that
/// is made at once; every other change waits for a second person.
/// </summary>
public sealed record PackageTerms(
    Guid ServiceItemId, decimal Price, Guid? TariffPlanId, PackageUse UsedFor, bool IsActive, IReadOnlyList<PackageCover> Covers)
{
    public static PackageTerms Of(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Create(package.ServiceItemId, package.Price, package.TariffPlanId, package.UsedFor, package.IsActive,
            package.Inclusions.Select(i => new PackageCover(i.ServiceItemId, i.Category, i.MaxQuantity, i.MaxAmount, i.Notes)));
    }

    /// <summary>The terms with the covers in a fixed order, so the same covers in another order are no change.</summary>
    public static PackageTerms Create(
        Guid serviceItemId, decimal price, Guid? tariffPlanId, PackageUse usedFor, bool isActive, IEnumerable<PackageCover>? covers) =>
        new(serviceItemId, price, tariffPlanId, usedFor, isActive,
            [.. (covers ?? []).OrderBy(c => c.ServiceItemId).ThenBy(c => c.Category).ThenBy(c => c.MaxQuantity).ThenBy(c => c.MaxAmount)]);

    /// <summary>
    /// A new package's terms until a second person approves the ones asked: out of use, at ₹0, covering nothing. The
    /// service it is billed as, its plan and the bills it may price are kept, as they price nobody while it is out of use.
    /// </summary>
    public PackageTerms OutOfUse() => this with { Price = 0m, IsActive = false, Covers = [] };

    /// <summary>What a save makes at once over these terms: only taking the package out of use.</summary>
    public PackageTerms Narrowed(PackageTerms asked)
    {
        ArgumentNullException.ThrowIfNull(asked);
        return this with { IsActive = IsActive && asked.IsActive };
    }

    /// <summary>Whether the covers' notes differ while what they absorb is the same.</summary>
    public bool NotesDiffer(PackageTerms other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return !Covers.Select(c => c.Notes).SequenceEqual(other.Covers.Select(c => c.Notes));
    }

    public bool Equals(PackageTerms? other) =>
        other is not null && ServiceItemId == other.ServiceItemId && Price == other.Price && TariffPlanId == other.TariffPlanId
        && UsedFor == other.UsedFor && IsActive == other.IsActive
        && Covers.Count == other.Covers.Count && Covers.Zip(other.Covers).All(pair => pair.First.SameRule(pair.Second));

    public override int GetHashCode() => HashCode.Combine(ServiceItemId, Price, TariffPlanId, UsedFor, IsActive, Covers.Count);

    public void ApplyTo(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.ServiceItemId = ServiceItemId;
        package.Price = Price;
        package.TariffPlanId = TariffPlanId;
        package.UsedFor = UsedFor;
        package.IsActive = IsActive;
        package.Inclusions.Clear();
        foreach (var cover in Covers)
        {
            package.Inclusions.Add(new PackageInclusion
            {
                PackageId = package.Id,
                ServiceItemId = cover.ServiceItemId,
                Category = cover.ServiceItemId is null ? cover.Category : null,
                MaxQuantity = cover.MaxQuantity,
                MaxAmount = cover.MaxAmount,
                Notes = string.IsNullOrWhiteSpace(cover.Notes) ? null : cover.Notes.Trim(),
            });
        }
    }
}

/// <summary>A package's terms asked for, and the terms they replace (a change made meanwhile is not overwritten).</summary>
public sealed record PackageTermsPayload(Guid PackageId, PackageTerms Asked, PackageTerms Replaced);

/// <summary>
/// A package's terms waiting for a second person, with the code of the service it would be billed as and its covers named,
/// as the package's form shows them.
/// </summary>
public sealed record PendingPackageTerms(
    Guid ApprovalId, PackageTerms Asked, string RequestedBy, DateTimeOffset RequestedAt, string? ServiceCode = null,
    IReadOnlyList<PackageInclusionDto>? Inclusions = null);

/// <summary>
/// A package's price and what it covers change only when two people agree (maker-checker): the billing desk asks, and
/// someone else who holds <see cref="PlatformPermissions.PricesApprove"/> makes the change, only over the terms it replaces.
/// A new package is made out of use and goes live with the terms approved. The approver is told how many stays in
/// progress are on the package, as their final bills take it as it is then.
/// </summary>
public sealed class PackageApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "billing.package";

    private const string NoReason = "No reason given.";

    /// <summary>Asks a second person to set <paramref name="package"/>'s terms to <paramref name="asked"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Package package, PackageTerms replaced, PackageTerms asked, string? reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(replaced);
        ArgumentNullException.ThrowIfNull(asked);
        var names = await NamesAsync(replaced, asked, ct);
        var changes = Changes(replaced, asked, names).ToList();
        List<ApprovalDetail> details = [new("Package", $"{package.Code} {package.Name}"), .. changes.Select(c => new ApprovalDetail(c.Label, c.Value))];
        var stays = await db.Set<IpAccount>().CountAsync(
            a => a.PackageId == package.Id && (a.Status == IpAccountStatus.Open || a.Status == IpAccountStatus.DischargeInitiated), ct);
        if (stays > 0)
        {
            // A stay's package is priced at its final bill from the package as it is then.
            details.Add(new("Stays on it", $"{stays} stay(s) in progress: their final bills take the package as approved"));
        }

        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Package), package.Id, package.Version, await FacilityAsync(ct), null),
            new PackageTermsPayload(package.Id, asked, replaced),
            Fit($"Package {package.Code} {package.Name}: {string.Join("; ", changes.Select(c => c.Words))}", 500),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>The terms waiting for approval for this package, if any.</summary>
    public async Task<PendingPackageTerms?> PendingAsync(Guid packageId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Package) && r.SubjectId == packageId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
        if (request is null)
        {
            return null;
        }

        var asked = ApprovalPayload.Read<PackageTermsPayload>(request.Payload).Asked;
        var ids = asked.Covers.Select(c => c.ServiceItemId).OfType<Guid>().Append(asked.ServiceItemId).Distinct().ToList();
        var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => new { s.Code, s.Name }, ct);
        return new PendingPackageTerms(request.Id, asked, request.RequestedByName, request.RequestedAt,
            services.GetValueOrDefault(asked.ServiceItemId)?.Code,
            [.. asked.Covers.Select(c => new PackageInclusionDto(
                Guid.Empty, c.ServiceItemId, c.ServiceItemId is { } code ? services.GetValueOrDefault(code)?.Code : null,
                c.ServiceItemId is { } name ? services.GetValueOrDefault(name)?.Name : null, c.Category, c.MaxQuantity, c.MaxAmount, c.Notes))]);
    }

    /// <summary>Sets an approved package's terms, in the approval's transaction, if they are still the ones the request replaces.</summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PackageTermsPayload>(request.Payload);
        var package = await db.Set<Package>().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == asked.PackageId, ct);
        if (package is null)
        {
            return Error.Rule("prices.package_gone", "The package no longer exists, so nothing was set.");
        }

        var before = PackageTerms.Of(package);
        if (before != asked.Replaced)
        {
            return Error.Rule(TariffApprovals.ChangedCode, $"Package {package.Code}'s terms changed after this was asked for, so nothing was set. Ask again.");
        }

        asked.Asked.ApplyTo(package);
        var words = string.Join("; ", Changes(before, asked.Asked, await NamesAsync(before, asked.Asked, ct)).Select(c => c.Words));
        audit.Add(new AuditEntry(AuditCategories.Admin, "package.terms_changed",
            Fit($"{package.Code} {package.Name}: {words} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(Package),
            EntityId = package.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy },
        });
        return words;
    }

    /// <summary>The names the approver reads changes in: services (code and name, with their GST rate) and plans.</summary>
    internal sealed record Names(IReadOnlyDictionary<Guid, (string Name, decimal TaxRate)> Services, IReadOnlyDictionary<Guid, string> Plans);

    /// <summary>What changes from <paramref name="from"/> to <paramref name="to"/>: a detail row and its words, each.</summary>
    internal static IEnumerable<(string Label, string Value, string Words)> Changes(PackageTerms from, PackageTerms to, Names names)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(names);
        if (from.IsActive != to.IsActive)
        {
            yield return to.IsActive
                ? ("In use", "Put in use: stays and out-patient bills may be priced on it", "put in use")
                : ("In use", "Taken out of use: no stay or bill may choose it; those on it keep it", "taken out of use");
        }

        if (from.Price != to.Price)
        {
            var value = $"{Rupees(from.Price)} → {Rupees(to.Price)}{(from.Price > 0m ? $" ({Signed((to.Price - from.Price) / from.Price * 100m)})" : string.Empty)}";
            yield return ("Price", value, $"price {value}");
        }

        if (from.ServiceItemId != to.ServiceItemId)
        {
            var value = $"{Service(from.ServiceItemId, names)} → {Service(to.ServiceItemId, names)}";
            yield return ("Billed as", value, $"billed as {Service(to.ServiceItemId, names)}");
        }

        if (from.TariffPlanId != to.TariffPlanId)
        {
            var value = $"{Plan(from.TariffPlanId, names)} → {Plan(to.TariffPlanId, names)}";
            yield return ("Priced for", value, $"for {Plan(to.TariffPlanId, names)}");
        }

        if (from.UsedFor != to.UsedFor)
        {
            var value = $"{Use(from.UsedFor)} → {Use(to.UsedFor)}";
            yield return ("Prices", value, $"prices {Use(to.UsedFor)}");
        }

        foreach (var cover in to.Covers.Where(c => !from.Covers.Any(f => f.SameRule(c))))
        {
            var what = Cover(cover, names);
            yield return ("Covers", $"Added: {what}", $"covers {what}");
        }

        foreach (var cover in from.Covers.Where(c => !to.Covers.Any(t => t.SameRule(c))))
        {
            var what = Cover(cover, names);
            yield return ("Covers", $"Taken away: {what}", $"no longer covers {what}");
        }
    }

    private async Task<Names> NamesAsync(PackageTerms from, PackageTerms to, CancellationToken ct)
    {
        var serviceIds = from.Covers.Concat(to.Covers).Select(c => c.ServiceItemId).OfType<Guid>().Append(from.ServiceItemId).Append(to.ServiceItemId).Distinct().ToList();
        var planIds = new[] { from.TariffPlanId, to.TariffPlanId }.OfType<Guid>().Distinct().ToList();
        var services = await db.Set<ServiceItem>().AsNoTracking().Where(s => serviceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => ($"{s.Code} {s.Name}", s.TaxRate), ct);
        var plans = planIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Set<TariffPlan>().AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return new Names(services, plans);
    }

    private async Task<Guid> FacilityAsync(CancellationToken ct) =>
        current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);

    private static string Service(Guid id, Names names) =>
        names.Services.TryGetValue(id, out var service) ? $"{service.Name} (GST {Percent(service.TaxRate)})" : "a service";

    private static string Plan(Guid? id, Names names) =>
        id is { } planId ? names.Plans.GetValueOrDefault(planId, "a tariff plan") + " only" : "any plan";

    private static string Use(PackageUse use) => use switch
    {
        PackageUse.OutPatient => "out-patient bills",
        PackageUse.Both => "stays and out-patient bills",
        _ => "stays",
    };

    private static string Cover(PackageCover cover, Names names)
    {
        var what = cover.ServiceItemId is { } id ? names.Services.TryGetValue(id, out var service) ? service.Name : "a service" : $"every {Words(cover.Category)} service";
        var caps = new[]
        {
            cover.MaxQuantity is { } quantity ? $"up to {quantity.ToString("0.##", CultureInfo.InvariantCulture)}" : null,
            cover.MaxAmount is { } amount ? $"up to {Rupees(amount)}" : null,
        }.OfType<string>().ToList();
        return caps.Count == 0 ? what : $"{what}, {string.Join(", ", caps)}";
    }

    /// <summary>A category in words: "BedCharge" → "bed charge".</summary>
    private static string Words(ServiceCategory? category) =>
        string.Concat((category?.ToString() ?? "other").Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string Rupees(decimal amount) => "₹" + Money.FormatInr(amount);

    private static string Percent(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    private static string Signed(decimal percent) => (percent > 0 ? "+" : percent < 0 ? "−" : string.Empty) + Math.Abs(percent).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A package's price or cover changed: the billing desk asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class PackageTermsApproval(PackageApprovals packages) : IApprovalAction
{
    public string Kind => PackageApprovals.Kind;

    public string Title => "Package";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => packages.ApplyAsync(request, ct);
}
