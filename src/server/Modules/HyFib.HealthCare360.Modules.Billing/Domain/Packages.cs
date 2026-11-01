namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>Which bills a package can price: an admission's final bill, an out-patient bill, or either.</summary>
public enum PackageUse
{
    InPatient = 0,
    OutPatient,
    Both,
}

/// <summary>
/// A fixed-price treatment package (MB-02, IPB-04, OPB): for example "Normal delivery — 2 days" or "Cataract (per eye)".
/// The package is billed as one line; the inclusions say which actual services it absorbs and up to what caps.
/// </summary>
public sealed class Package : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Catalogue item (category Package) billed for the package itself.</summary>
    public Guid ServiceItemId { get; set; }

    public decimal Price { get; set; }
    public int? LengthOfStayDays { get; set; }

    /// <summary>Tariff plan the package price belongs to (<c>null</c> = any plan).</summary>
    public Guid? TariffPlanId { get; set; }

    /// <summary>
    /// Admissions, out-patient visits (day-care procedures, minor surgery, dental work) or both. Packages made before
    /// out-patient bills could use packages are in-patient packages.
    /// </summary>
    public PackageUse UsedFor { get; set; } = PackageUse.InPatient;

    public string? Description { get; set; }

    /// <summary>
    /// The AB PM-JAY procedure the package is billed as for the scheme (an <see cref="HbpProcedure"/> code, as
    /// <c>SG039A</c>), which claims to the scheme name in place of HC360's own code. Null for packages the scheme does not cover.
    /// </summary>
    public string? HbpCode { get; set; }

    public bool IsActive { get; set; } = true;
    public List<PackageInclusion> Inclusions { get; set; } = [];

    public IReadOnlyList<PackageRule> Rules() =>
        Inclusions.Select(i => new PackageRule(i.ServiceItemId, i.Category, i.MaxQuantity, i.MaxAmount)).ToList();

    /// <summary>True when the package may price a bill of this type.</summary>
    public bool CanBill(BillType type) =>
        UsedFor == PackageUse.Both || UsedFor == (type == BillType.InPatient ? PackageUse.InPatient : PackageUse.OutPatient);
}

/// <summary>
/// What a package covers: one service item or a whole category, optionally capped by quantity and/or amount. Saving the
/// package replaces them, so its audit event lists them as they were and as they are (<c>inclusions</c>).
/// </summary>
[AuditedWithParent]
public sealed class PackageInclusion : Entity
{
    public Guid PackageId { get; set; }
    public Guid? ServiceItemId { get; set; }
    public ServiceCategory? Category { get; set; }
    public decimal? MaxQuantity { get; set; }
    public decimal? MaxAmount { get; set; }
    public string? Notes { get; set; }
}

public sealed record PackageRule(Guid? ServiceItemId, ServiceCategory? Category, decimal? MaxQuantity, decimal? MaxAmount);

/// <summary>An actual service delivered during the stay (<paramref name="Key"/> identifies the bill line).</summary>
public sealed record PackageUsage(Guid Key, Guid ServiceItemId, ServiceCategory Category, decimal Quantity, decimal UnitPrice, decimal Gross, DateTimeOffset ServiceDate);

public sealed record PackageAllocation(Guid Key, decimal Covered, decimal Excess, int? RuleIndex);

public sealed record PackageOutcome(decimal PackagePrice, IReadOnlyList<PackageAllocation> Lines, decimal Actuals, decimal Covered, decimal Excess)
{
    /// <summary>Package price minus the actual value it absorbed: positive = the hospital gains, negative = a loss.</summary>
    public decimal Variance => PackagePrice - Covered;
}

/// <summary>
/// Package vs actuals (IPB-04). Services named in the package are absorbed up to each inclusion's quantity/amount cap,
/// earliest service first; the remainder and anything not included is excess, billed separately. An item-specific
/// inclusion governs its item exclusively; category inclusions cover the other items of that category. A unit that is
/// only partly absorbed by an amount cap still counts against the quantity cap. Returns (negative lines) undo
/// consumption under their inclusion, so the package never pays for goods that came back.
/// </summary>
public static class PackageCalculator
{
    public static PackageOutcome Allocate(decimal packagePrice, IReadOnlyList<PackageRule> rules, IEnumerable<PackageUsage> usages)
    {
        var remainingQuantity = rules.Select(r => r.MaxQuantity).ToArray();
        var remainingAmount = rules.Select(r => r.MaxAmount).ToArray();
        var ledger = rules.Select(_ => new RuleLedger()).ToArray();
        var allocations = new List<PackageAllocation>();

        foreach (var usage in usages.OrderBy(u => u.ServiceDate).ThenBy(u => u.Key))
        {
            var index = FindRule(rules, usage);
            if (usage.Gross < 0)
            {
                allocations.Add(Return(usage, index, ledger, remainingQuantity, remainingAmount));
                continue;
            }

            if (index < 0 || usage.Gross == 0 || usage.Quantity <= 0)
            {
                allocations.Add(new PackageAllocation(usage.Key, 0m, usage.Gross, index < 0 ? null : index));
                continue;
            }

            var quantity = remainingQuantity[index] is { } qtyLeft ? Math.Min(Math.Max(qtyLeft, 0m), usage.Quantity) : usage.Quantity;
            var covered = quantity >= usage.Quantity ? usage.Gross : Money.Round(quantity * usage.UnitPrice);
            if (remainingAmount[index] is { } amountLeft)
            {
                covered = Math.Min(covered, Math.Max(amountLeft, 0m));
            }

            covered = Math.Clamp(covered, 0m, usage.Gross);
            if (covered > 0)
            {
                if (remainingQuantity[index] is not null)
                {
                    remainingQuantity[index] -= quantity;
                }

                if (remainingAmount[index] is not null)
                {
                    remainingAmount[index] -= covered;
                }

                ledger[index].CoveredQuantity += quantity;
            }

            ledger[index].Covered += covered;
            ledger[index].Excess += usage.Gross - covered;
            allocations.Add(new PackageAllocation(usage.Key, covered, usage.Gross - covered, index));
        }

        return new PackageOutcome(
            Money.Round(packagePrice),
            allocations,
            allocations.Sum(a => a.Covered + a.Excess),
            allocations.Sum(a => a.Covered),
            allocations.Sum(a => a.Excess));
    }

    /// <summary>
    /// A return (negative gross, e.g. unused drugs sent back to pharmacy) undoes consumption under its inclusion, latest
    /// first: it refunds what the patient paid beyond the cap before it gives the package its allowance back, exactly as if
    /// the returned goods had never been issued. Returns of services the package does not include are credited in full.
    /// </summary>
    private static PackageAllocation Return(PackageUsage usage, int index, RuleLedger[] ledger, decimal?[] remainingQuantity, decimal?[] remainingAmount)
    {
        if (index < 0)
        {
            return new PackageAllocation(usage.Key, 0m, usage.Gross, null);
        }

        var returned = -usage.Gross;
        var fromExcess = Math.Min(returned, Math.Max(ledger[index].Excess, 0m));
        var fromCover = Math.Min(returned - fromExcess, Math.Max(ledger[index].Covered, 0m));
        if (fromCover > 0)
        {
            var quantity = Math.Min(Math.Abs(usage.Quantity), ledger[index].CoveredQuantity);
            if (remainingQuantity[index] is not null)
            {
                remainingQuantity[index] += quantity;
            }

            if (remainingAmount[index] is not null)
            {
                remainingAmount[index] += fromCover;
            }

            ledger[index].CoveredQuantity -= quantity;
        }

        ledger[index].Covered -= fromCover;
        ledger[index].Excess -= returned - fromCover;
        return new PackageAllocation(usage.Key, -fromCover, usage.Gross + fromCover, index);
    }

    private static int FindRule(IReadOnlyList<PackageRule> rules, PackageUsage usage)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].ServiceItemId == usage.ServiceItemId)
            {
                return i;
            }
        }

        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].ServiceItemId is null && rules[i].Category == usage.Category)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>What one inclusion has absorbed and left to the patient so far (needed to undo it on a return).</summary>
    private sealed class RuleLedger
    {
        public decimal Covered { get; set; }

        public decimal Excess { get; set; }

        public decimal CoveredQuantity { get; set; }
    }
}
