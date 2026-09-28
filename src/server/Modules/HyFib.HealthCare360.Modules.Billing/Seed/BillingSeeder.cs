using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Billing.Seed;

/// <summary>
/// Billing reference data: the default "General" tariff plan (always). With <c>HealthCare360:SeedDemoData</c>: demo
/// insurers, TPAs and government schemes (CGHS/ECHS with scheme tariffs), a delivery package and doctor share rules.
/// Idempotent: every row is looked up by its code first.
/// </summary>
internal sealed class BillingSeeder(IOptions<PlatformOptions> options, TimeProvider time) : ISeeder
{
    public const string GeneralPlanCode = "GEN";

    public int Order => 400;

    private static readonly (string Code, string Name, PayerCategory Category, int CreditDays)[] DemoPayers =
    [
        ("STAR", "Star Health and Allied Insurance", PayerCategory.Insurer, 30),
        ("HDFCERGO", "HDFC ERGO General Insurance", PayerCategory.Insurer, 30),
        ("ICICILOM", "ICICI Lombard General Insurance", PayerCategory.Insurer, 30),
        ("NIA", "The New India Assurance Co.", PayerCategory.Insurer, 45),
        ("MEDIASSIST", "Medi Assist Insurance TPA", PayerCategory.Tpa, 30),
        ("MDINDIA", "MD India Health Insurance TPA", PayerCategory.Tpa, 30),
        ("PARAMOUNT", "Paramount Health Services & Insurance TPA", PayerCategory.Tpa, 30),
        ("PMJAY", "Ayushman Bharat PM-JAY", PayerCategory.GovernmentScheme, 60),
        ("CGHS", "Central Government Health Scheme (CGHS)", PayerCategory.GovernmentScheme, 60),
        ("ECHS", "Ex-Servicemen Contributory Health Scheme (ECHS)", PayerCategory.GovernmentScheme, 60),
    ];

    /// <summary>Illustrative scheme rates (CGHS publishes its own; ECHS follows CGHS).</summary>
    private static readonly (string Service, decimal Price)[] SchemeRates =
    [
        ("CONS-NEW", 350m), ("CONS-FU", 350m), ("BED-GEN", 1000m), ("BED-SEMI", 2000m), ("BED-PVT", 3000m), ("VISIT-DR", 350m),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Set<TariffPlan>().IgnoreQueryFilters().AnyAsync(p => p.Code == GeneralPlanCode, cancellationToken))
        {
            db.Add(new TariffPlan
            {
                Code = GeneralPlanCode,
                Name = "General",
                Type = TariffType.General,
                IsDefault = true,
                Description = "Prices for self-paying patients; services without a plan price use the catalogue base price.",
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!options.Value.SeedDemoData)
        {
            return;
        }

        await SeedPayersAsync(db, cancellationToken);
        await SeedPackageAsync(db, cancellationToken);
        await SeedShareRulesAsync(db, cancellationToken);
    }

    private async Task SeedPayersAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<Payer>().IgnoreQueryFilters().Select(p => p.Code).ToListAsync(ct);
        var rateCodes = SchemeRates.Select(r => r.Service).ToList();
        var services = await db.Set<ServiceItem>().Where(s => rateCodes.Contains(s.Code)).ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        var effectiveFrom = new DateOnly(BillingTime.Today(time).Year, 1, 1);

        foreach (var (code, name, category, creditDays) in DemoPayers.Where(p => !existing.Contains(p.Code)))
        {
            var payer = new Payer { Code = code, Name = name, Category = category, CreditDays = creditDays };
            if (code is "CGHS" or "ECHS")
            {
                var plan = new TariffPlan { Code = code, Name = $"{code} rates", Type = TariffType.GovernmentScheme, PayerId = payer.Id };
                db.Add(plan);
                payer.TariffPlanId = plan.Id;
                foreach (var (service, price) in SchemeRates.Where(r => services.ContainsKey(r.Service)))
                {
                    db.Add(new TariffPrice { TariffPlanId = plan.Id, ServiceItemId = services[service], Price = price, EffectiveFrom = effectiveFrom });
                }
            }

            db.Add(payer);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedPackageAsync(HospitalDbContext db, CancellationToken ct)
    {
        const string code = "PKG-NVD";
        if (await db.Set<Package>().IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
        {
            return;
        }

        var packageItem = await db.Set<ServiceItem>().FirstOrDefaultAsync(s => s.Code == code, ct);
        if (packageItem is null)
        {
            packageItem = new ServiceItem
            {
                Code = code,
                Name = "Normal delivery package (2 days, general ward)",
                Category = ServiceCategory.Package,
                BasePrice = 35000m,
                SacCode = "999311",
                IsDiscountAllowed = false,
            };
            db.Add(packageItem);
        }

        var inclusions = await db.Set<ServiceItem>().Where(s => s.Code == "BED-GEN" || s.Code == "NURS-DAY" || s.Code == "VISIT-DR").ToDictionaryAsync(s => s.Code, s => s.Id, ct);
        var package = new Package
        {
            Code = code,
            Name = "Normal delivery package",
            ServiceItemId = packageItem.Id,
            Price = 35000m,
            LengthOfStayDays = 2,
            Description = "Two days in the general ward with nursing, consultant visits, routine investigations and consumables.",
        };
        foreach (var (service, quantity) in new[] { ("BED-GEN", 2m), ("NURS-DAY", 2m), ("VISIT-DR", 4m) })
        {
            if (inclusions.TryGetValue(service, out var serviceItemId))
            {
                package.Inclusions.Add(new PackageInclusion { PackageId = package.Id, ServiceItemId = serviceItemId, MaxQuantity = quantity });
            }
        }

        package.Inclusions.Add(new PackageInclusion { PackageId = package.Id, Category = ServiceCategory.Laboratory, MaxAmount = 3000m, Notes = "Routine investigations" });
        package.Inclusions.Add(new PackageInclusion { PackageId = package.Id, Category = ServiceCategory.Consumable, MaxAmount = 2500m, Notes = "Consumables" });
        db.Add(package);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedShareRulesAsync(HospitalDbContext db, CancellationToken ct)
    {
        if (await db.Set<DoctorShareRule>().IgnoreQueryFilters().AnyAsync(ct))
        {
            return;
        }

        var from = new DateOnly(BillingTime.Today(time).Year, 1, 1);
        var defaults = new (ServiceCategory Category, decimal Percent)[]
        {
            (ServiceCategory.Consultation, 80m),
            (ServiceCategory.DoctorVisit, 80m),
            (ServiceCategory.Procedure, 30m),
            (ServiceCategory.Surgery, 40m),
        };
        foreach (var (category, percent) in defaults)
        {
            db.Add(new DoctorShareRule { Category = category, Method = ShareMethod.Percentage, Value = percent, EffectiveFrom = from, Notes = "Hospital default" });
        }

        // Example of a doctor-specific rule beating the default: government-scheme consultations at a fixed fee.
        var cardiologist = await db.Set<Practitioner>().Where(p => p.Code == "DR005").Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        if (cardiologist is { } doctorId)
        {
            db.Add(new DoctorShareRule
            {
                PractitionerId = doctorId,
                Category = ServiceCategory.Consultation,
                PayerType = PayerType.GovernmentScheme,
                Method = ShareMethod.FixedPerUnit,
                Value = 250m,
                EffectiveFrom = from,
                Notes = "Scheme consultations",
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
