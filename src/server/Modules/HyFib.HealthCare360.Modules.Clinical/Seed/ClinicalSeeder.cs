using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Clinical.Seed;

/// <summary>
/// Reference data for the Clinical module (idempotent; runs on every start): ICD-10 and ICD-10-PCS catalogues, the
/// drug-interaction and maximum-dose tables, speciality templates, order sets and health packages.
/// <para>
/// Order 300 so the Diagnostics seeder (200) has created the LAB-/RAD-/CARD- service codes the templates and packages
/// reference. Codes still missing are skipped and logged; entries that no user has edited yet (UpdatedBy is null) are
/// topped up on a later start, while anything an administrator changed is left alone.
/// </para>
/// </summary>
internal sealed class ClinicalSeeder(ILogger<ClinicalSeeder> logger) : ISeeder
{
    public int Order => 300;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        await SeedIcdCodesAsync(db, cancellationToken);
        await SeedProcedureCodesAsync(db, cancellationToken);
        await SeedInteractionsAsync(db, cancellationToken);
        await SeedDoseLimitsAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        // All codes (active or not) so package registration never collides with the unique service code.
        var services = await db.Set<ServiceItem>().AsNoTracking().ToDictionaryAsync(s => s.Code, cancellationToken);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        await SeedTemplatesAsync(db, services, missing, cancellationToken);
        await SeedOrderSetsAsync(db, services, missing, cancellationToken);
        await SeedPackagesAsync(db, services, missing, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (missing.Count > 0)
        {
            logger.LogInformation("Clinical seed skipped service codes not yet in the catalogue: {Codes}", string.Join(", ", missing));
        }
    }

    /// <summary>
    /// Adds the starter codes the catalogue does not have yet. A code an import withdrew or that was deleted still counts
    /// as present, so it is not added back.
    /// </summary>
    private async Task SeedIcdCodesAsync(HospitalDbContext db, CancellationToken ct)
    {
        await NormalizeReleaseLabelsAsync(db, ct);
        var existing = (await db.Set<IcdCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(c => c.Release == CodeReleases.Icd10).Select(c => c.Code).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in IcdCodeCatalogue.All.Where(e => !existing.Contains(e.Code)))
        {
            var code = new IcdCode
            {
                Code = entry.Code,
                Title = entry.Title,
                Release = CodeReleases.Icd10,
                Chapter = IcdClassification.ChapterOf(entry.Code)?.Number,
                Block = IcdClassification.BlockOf(entry.Code)?.Range,
                IsBillable = entry.Billable,
                Keywords = entry.Keywords,
                Source = CodeSource.Seed,
            };
            code.RefreshSearchText();
            db.Add(code);
        }
    }

    /// <summary>
    /// Codes saved from the WHO online search used to keep the API's release label ("ICD-11 MMS 2025-01"), which the rest
    /// of HC360 did not recognise. They move to the catalogue's own label ("ICD-11 MMS"), unless the code is already
    /// there under it.
    /// </summary>
    private async Task NormalizeReleaseLabelsAsync(HospitalDbContext db, CancellationToken ct)
    {
        string[] canonical = [CodeReleases.Icd10, CodeReleases.Icd11, CodeReleases.Icd10Pcs];
        var odd = await db.Set<IcdCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(c => !canonical.Contains(c.Release)).ToListAsync(ct);
        if (odd.Count == 0)
        {
            return;
        }

        var taken = (await db.Set<IcdCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
                .Where(c => canonical.Contains(c.Release)).Select(c => new { c.Code, c.Release }).ToListAsync(ct))
            .Select(c => (c.Code, c.Release)).ToHashSet();
        var moved = 0;
        foreach (var code in odd)
        {
            if (CodeReleases.Normalize(code.Release) is { } release and (CodeReleases.Icd10 or CodeReleases.Icd11) && taken.Add((code.Code, release)))
            {
                code.Release = release;
                moved++;
            }
        }

        if (moved > 0)
        {
            logger.LogInformation("Moved {Count} ICD codes saved from the online search to the catalogue's release labels", moved);
        }
    }

    private static async Task SeedProcedureCodesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = (await db.Set<ProcedureCode>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
                .Where(c => c.System == CodeReleases.Icd10Pcs).Select(c => c.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in ProcedureCodeCatalogue.All.Where(e => !existing.Contains(e.Code)))
        {
            var code = new ProcedureCode
            {
                Code = entry.Code,
                Title = entry.Title,
                System = CodeReleases.Icd10Pcs,
                Category = entry.Category,
                Keywords = entry.Keywords,
                Source = CodeSource.Seed,
            };
            code.RefreshSearchText();
            db.Add(code);
        }
    }

    /// <summary>Stored canonical and alphabetically ordered, matching what the API does for new rows.</summary>
    private static async Task SeedInteractionsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = (await db.Set<DrugInteraction>().Select(i => new { i.DrugA, i.DrugB }).ToListAsync(ct))
            .Select(i => (i.DrugA, i.DrugB)).ToHashSet();
        foreach (var seed in DrugSafetyCatalogue.Interactions)
        {
            var (a, b) = CanonicalPair(seed.DrugA, seed.DrugB);
            if (!existing.Add((a, b)))
            {
                continue;
            }

            db.Add(new DrugInteraction { DrugA = a, DrugB = b, Severity = seed.Severity, Effect = seed.Effect, Management = seed.Management });
        }
    }

    private static async Task SeedDoseLimitsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = (await db.Set<DrugDoseLimit>().Select(l => new { l.GenericName, l.Route }).ToListAsync(ct))
            .Select(l => (l.GenericName, l.Route)).ToHashSet();
        foreach (var seed in DrugSafetyCatalogue.DoseLimits)
        {
            var name = DrugNames.Components(seed.GenericName).FirstOrDefault() ?? seed.GenericName;
            if (existing.Add((name, seed.Route)))
            {
                db.Add(new DrugDoseLimit { GenericName = name, MaxDailyDoseMg = seed.MaxDailyDoseMg, Route = seed.Route, Notes = seed.Notes });
            }
        }
    }

    private static async Task SeedTemplatesAsync(HospitalDbContext db, Dictionary<string, ServiceItem> services, SortedSet<string> missing, CancellationToken ct)
    {
        var existing = await db.Set<ClinicalTemplate>().Include(t => t.Medications).ToDictionaryAsync(t => t.Code, ct);
        foreach (var seed in TemplateCatalogue.Templates)
        {
            var serviceIds = Resolve(seed.OrderCodes, services, missing);
            if (existing.TryGetValue(seed.Code, out var template))
            {
                // Top up default orders that became available, unless an administrator has edited the template.
                if (template.UpdatedBy is null && serviceIds.Except(template.DefaultServiceItemIds).Any())
                {
                    template.DefaultServiceItemIds = template.DefaultServiceItemIds.Union(serviceIds).ToList();
                }

                continue;
            }

            template = new ClinicalTemplate
            {
                Code = seed.Code,
                Name = seed.Name,
                SpecialtyCode = seed.Specialty,
                HistoryOfPresentIllness = seed.HistoryPrompt,
                Examination = seed.Examination,
                Plan = seed.Plan,
                FollowUpAdvice = seed.FollowUpAdvice,
                FollowUpDays = seed.FollowUpDays,
                CommonComplaints = [.. seed.Complaints],
                DefaultServiceItemIds = serviceIds,
                DisplayOrder = seed.DisplayOrder,
            };
            var sequence = 0;
            foreach (var m in seed.Medications)
            {
                template.Medications.Add(new TemplateMedication
                {
                    TemplateId = template.Id,
                    Sequence = ++sequence,
                    DrugName = m.DrugName,
                    GenericName = m.GenericName,
                    Strength = m.Strength,
                    DosageForm = m.DosageForm,
                    Route = m.Route,
                    Dose = m.Dose,
                    Frequency = m.Frequency,
                    DurationDays = m.DurationDays,
                    Instructions = m.Instructions,
                });
            }

            db.Add(template);
        }
    }

    private static async Task SeedOrderSetsAsync(HospitalDbContext db, Dictionary<string, ServiceItem> services, SortedSet<string> missing, CancellationToken ct)
    {
        var existing = await db.Set<OrderSet>().ToDictionaryAsync(s => s.Code, ct);
        foreach (var seed in TemplateCatalogue.OrderSets)
        {
            var serviceIds = Resolve(seed.ServiceCodes, services, missing);
            if (existing.TryGetValue(seed.Code, out var set))
            {
                if (set.UpdatedBy is null && serviceIds.Except(set.ServiceItemIds).Any())
                {
                    set.ServiceItemIds = set.ServiceItemIds.Union(serviceIds).ToList();
                }

                continue;
            }

            if (serviceIds.Count == 0)
            {
                continue; // Nothing orderable yet; created on a later start once Diagnostics has seeded its services.
            }

            db.Add(new OrderSet { Code = seed.Code, Name = seed.Name, SpecialtyCode = seed.Specialty, Description = seed.Description, ServiceItemIds = serviceIds });
        }
    }

    private static async Task SeedPackagesAsync(HospitalDbContext db, Dictionary<string, ServiceItem> services, SortedSet<string> missing, CancellationToken ct)
    {
        var existing = await db.Set<HealthPackage>().Include(p => p.Items).ToDictionaryAsync(p => p.Code, ct);
        foreach (var seed in HealthPackageCatalogue.Packages)
        {
            if (!existing.TryGetValue(seed.Code, out var package))
            {
                package = new HealthPackage
                {
                    Code = seed.Code,
                    Name = seed.Name,
                    Description = seed.Description,
                    Gender = seed.Gender,
                    MinAgeYears = seed.MinAgeYears,
                    MaxAgeYears = seed.MaxAgeYears,
                    Price = seed.Price,
                };
                db.Add(package);
                package.ServiceItemId = RegisterServiceItem(db, package, services);
            }
            else if (package.UpdatedBy is not null)
            {
                continue; // Edited by the hospital: never overwrite.
            }

            var sequence = package.Items.Count == 0 ? 0 : package.Items.Max(i => i.Sequence);
            foreach (var item in seed.Items)
            {
                if (!services.TryGetValue(item.ServiceCode, out var service) || !service.IsActive)
                {
                    missing.Add(item.ServiceCode);
                    continue;
                }

                var already = package.Items.Any(i => i.ServiceCode == item.ServiceCode && i.DepartmentCode == item.ConsultationDepartment);
                if (already)
                {
                    continue;
                }

                package.Items.Add(new HealthPackageItem
                {
                    PackageId = package.Id,
                    Kind = item.IsConsultation ? PackageItemKind.Consultation : PackageItemKind.Investigation,
                    ServiceItemId = service.Id,
                    ServiceCode = service.Code,
                    Name = item.IsConsultation ? $"Consultation — {DepartmentName(item.ConsultationDepartment!)}" : service.Name,
                    Category = service.Category,
                    DepartmentCode = item.ConsultationDepartment,
                    Gender = item.Gender,
                    MinAgeYears = item.MinAgeYears,
                    Sequence = ++sequence,
                });
            }
        }
    }

    /// <summary>Each package is billable as one service item (category HealthCheckup) with the package price.</summary>
    private static Guid RegisterServiceItem(HospitalDbContext db, HealthPackage package, Dictionary<string, ServiceItem> services)
    {
        if (services.TryGetValue(package.Code, out var existing))
        {
            return existing.Id;
        }

        var service = new ServiceItem
        {
            Code = package.Code,
            Name = package.Name,
            ShortName = package.Code,
            Category = ServiceCategory.HealthCheckup,
            BasePrice = package.Price,
            SacCode = "999312",
            Description = package.Description,
        };
        db.Add(service);
        return service.Id;
    }

    private static List<Guid> Resolve(IEnumerable<string> codes, Dictionary<string, ServiceItem> services, SortedSet<string> missing)
    {
        var ids = new List<Guid>();
        foreach (var code in codes)
        {
            if (services.TryGetValue(code, out var service) && service.IsActive)
            {
                ids.Add(service.Id);
            }
            else
            {
                missing.Add(code);
            }
        }

        return ids;
    }

    /// <summary>Display names of the departments seeded by the platform (consultations are listed by name on the itinerary).</summary>
    private static string DepartmentName(string code) => code switch
    {
        "GM" => "General Medicine",
        "OBG" => "Obstetrics & Gynaecology",
        "CARD" => "Cardiology",
        "PED" => "Paediatrics",
        "ORTH" => "Orthopaedics",
        "ENT" => "ENT",
        _ => code,
    };

    internal static (string A, string B) CanonicalPair(string drugA, string drugB)
    {
        var a = DrugNames.Components(drugA).FirstOrDefault() ?? drugA.Trim().ToLowerInvariant();
        var b = DrugNames.Components(drugB).FirstOrDefault() ?? drugB.Trim().ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
    }
}
