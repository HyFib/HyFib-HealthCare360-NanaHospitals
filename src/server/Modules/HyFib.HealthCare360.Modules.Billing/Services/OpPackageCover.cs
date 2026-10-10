using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Package vs actuals on an OP bill (MB-02, OPB). The bill's package absorbs the services it includes, up to each
/// inclusion's cap, exactly as an admission's package does on its final bill (<see cref="PackageCalculator"/>); anything
/// else is billed as usual. Lines of category Package (the package itself, a health checkup) are never absorbed. Run
/// after every change to a draft's lines. Does not save.
/// </summary>
internal static class OpPackageCover
{
    public static async Task ApplyAsync(HospitalDbContext db, Bill bill, CancellationToken ct)
    {
        if (bill.Type != BillType.OutPatient)
        {
            // An admission's package is applied when its final bill is prepared (IpBillingService).
            bill.Recalculate();
            return;
        }

        // A package taken out of use after it was chosen still prices the drafts that use it.
        var package = bill.PackageId is { } id
            ? await db.Set<Package>().AsNoTracking().Include(p => p.Inclusions).FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;
        if (package is null)
        {
            bill.ApplyPackageCoverage(new Dictionary<Guid, decimal>());
        }
        else
        {
            var usages = bill.Lines
                .Where(l => l.Category != ServiceCategory.Package)
                .Select(l => new PackageUsage(l.Id, l.ServiceItemId, l.Category, l.Quantity, l.UnitPrice, l.GrossAmount, l.ServiceDate));
            var outcome = PackageCalculator.Allocate(package.Price, package.Rules(), usages);
            bill.ApplyPackageCoverage(outcome.Lines.ToDictionary(a => a.Key, a => a.Covered));
        }

        bill.Recalculate();
    }
}
