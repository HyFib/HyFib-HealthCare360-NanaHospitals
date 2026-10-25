using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>
/// Diagnostics reference data: billable services (<c>LAB-*</c>, <c>RAD-*</c>, <c>CARD-*</c>), the laboratory test master with
/// panels, reference intervals and critical limits, imaging procedures, radiology report templates, and the extra
/// notification templates this module sends. It runs on every start and only inserts what is missing (by code).
/// Rows the laboratory or radiology have edited are never overwritten.
/// </summary>
internal sealed class DiagnosticsSeeder : ISeeder
{
    /// <summary>GST SAC 999316: medical laboratory and diagnostic-imaging services (exempt healthcare).</summary>
    public const string SacCode = "999316";

    private static readonly (string Key, NotificationChannel Channel, string? Subject, string Body, string Description)[] Templates =
    [
        (ReportDelivery.ReportReadyTemplate, NotificationChannel.Email, "Your report {{reference}} is ready",
            "<p>Dear {{name}},</p><p>Your diagnostic report <strong>{{reference}}</strong> is ready. A copy is attached, and you can also view and download it securely on the patient portal.</p><p>Regards,<br/>{{hospital}}</p>",
            "Diagnostic report ready (e-mail)"),
        (CriticalValueNotifier.FindingCriticalTemplate, NotificationChannel.Sms, null,
            "HC360 CRITICAL FINDING: {{patient}} ({{uhid}}) {{study}}: {{finding}}. Please review immediately.",
            "Critical imaging/cardiology finding to the ordering doctor"),
    ];

    public int Order => 200;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var facilityId = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => (Guid?)f.Id).FirstOrDefaultAsync(cancellationToken);
        var departments = facilityId is { } id
            ? await db.Set<Department>().Where(d => d.FacilityId == id).ToDictionaryAsync(d => d.Code, d => d.Id, cancellationToken)
            : new Dictionary<string, Guid>();

        var services = await SeedServiceItemsAsync(db, departments, cancellationToken);
        await SeedLabTestsAsync(db, services, cancellationToken);
        await SeedImagingProceduresAsync(db, services, cancellationToken);
        await SeedReportTemplatesAsync(db, cancellationToken);
        await SeedNotificationTemplatesAsync(db, cancellationToken);
    }

    private static async Task<Dictionary<string, Guid>> SeedServiceItemsAsync(
        HospitalDbContext db, IReadOnlyDictionary<string, Guid> departments, CancellationToken ct)
    {
        var catalogue = LabCatalog.Orderables
            .Select(o => new ServiceSeed(o.Code, o.Name, o.ShortName, ServiceCategory.Laboratory, o.Price, "LAB"))
            .Concat(ImagingCatalog.Procedures.Select(p => new ServiceSeed(p.Code, p.Name, p.ShortName, ServiceCategory.Radiology, p.Price, "RAD")))
            .Concat(ImagingCatalog.Cardiology.Select(c => new ServiceSeed(c.Code, c.Name, c.ShortName, ServiceCategory.Cardiology, c.Price, "CDX")))
            .ToList();
        var codes = catalogue.Select(c => c.Code).ToList();

        // Soft-deleted rows count as existing: re-creating them would break the unique code and undo an administrator's decision.
        var existing = await db.Set<ServiceItem>().IgnoreQueryFilters().Where(s => codes.Contains(s.Code)).Select(s => s.Code).ToListAsync(ct);
        foreach (var service in catalogue.Where(c => !existing.Contains(c.Code)))
        {
            db.Add(new ServiceItem
            {
                Code = service.Code,
                Name = service.Name,
                ShortName = service.ShortName,
                Category = service.Category,
                BasePrice = service.Price,
                DepartmentId = departments.TryGetValue(service.Department, out var departmentId) ? departmentId : null,
                SacCode = SacCode,
                IsDiscountAllowed = true,
            });
        }

        await db.SaveChangesAsync(ct);
        return await db.Set<ServiceItem>().Where(s => codes.Contains(s.Code)).ToDictionaryAsync(s => s.Code, s => s.Id, ct);
    }

    private static async Task SeedLabTestsAsync(HospitalDbContext db, IReadOnlyDictionary<string, Guid> services, CancellationToken ct)
    {
        var tests = await db.Set<LabTest>().IgnoreQueryFilters().ToDictionaryAsync(t => t.Code, t => t.Id, ct);
        var newPanels = new List<(LabTest Panel, OrderableSeed Seed)>();
        var displayOrder = 0;

        foreach (var analyte in LabCatalog.Analytes)
        {
            displayOrder++;
            if (tests.ContainsKey(analyte.Code))
            {
                continue;
            }

            var test = new LabTest
            {
                Code = analyte.Code,
                Name = analyte.Name,
                Section = analyte.Section,
                SpecimenType = analyte.Specimen,
                Container = analyte.Container,
                Loinc = analyte.Loinc,
                IsOrderable = false,
                DisplayOrder = displayOrder,
            };
            ApplyResult(test, analyte.Result);
            db.Add(test);
            tests[test.Code] = test.Id;
        }

        foreach (var orderable in LabCatalog.Orderables)
        {
            displayOrder++;
            if (tests.ContainsKey(orderable.Code))
            {
                continue;
            }

            var test = new LabTest
            {
                Code = orderable.Code,
                Name = orderable.Name,
                ShortName = orderable.ShortName,
                Section = orderable.Section,
                SpecimenType = orderable.Specimen,
                Container = orderable.Container,
                Method = orderable.Method,
                TatHours = orderable.TatHours,
                Loinc = orderable.Loinc,
                ServiceItemId = services.TryGetValue(orderable.Code, out var serviceItemId) ? serviceItemId : null,
                IsOrderable = true,
                IsPanel = orderable.IsPanel,
                ResultType = LabResultType.Text,
                Preparation = orderable.Preparation,
                Interpretation = orderable.Interpretation,
                DrawSeparately = orderable.DrawSeparately,
                DisplayOrder = displayOrder,
            };
            if (orderable.Result is { } result)
            {
                ApplyResult(test, result);
            }

            db.Add(test);
            tests[test.Code] = test.Id;
            if (orderable.IsPanel)
            {
                newPanels.Add((test, orderable));
            }
        }

        foreach (var (panel, seed) in newPanels)
        {
            var position = 0;
            foreach (var (code, group) in seed.Components)
            {
                // Catalogue consistency (every component exists) is covered by LabCatalogTests.
                if (tests.TryGetValue(code, out var componentId))
                {
                    db.Add(new LabTestComponent { PanelTestId = panel.Id, TestId = componentId, DisplayOrder = ++position, GroupName = group });
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static void ApplyResult(LabTest test, ResultSeed result)
    {
        test.ResultType = result.Type;
        test.Unit = result.Unit;
        test.Decimals = result.Decimals;
        test.Options = [.. result.Options ?? []];
        test.InstrumentChannelCodes = [.. result.Channels ?? []];
        test.DeltaThresholdPercent = result.DeltaPercent;
        test.QcGroup = result.QcGroup;

        // Only numeric analytes are candidates for auto-validation; the policy applies its other conditions at run time.
        test.AllowAutoValidation = result.Type == LabResultType.Numeric;
        foreach (var range in result.Ranges)
        {
            test.Ranges.Add(range.ToEntity(test.Id));
        }
    }

    private static async Task SeedImagingProceduresAsync(HospitalDbContext db, IReadOnlyDictionary<string, Guid> services, CancellationToken ct)
    {
        var existing = await db.Set<ImagingProcedure>().IgnoreQueryFilters().Select(p => p.Code).ToListAsync(ct);
        foreach (var procedure in ImagingCatalog.Procedures.Where(p => !existing.Contains(p.Code)))
        {
            db.Add(new ImagingProcedure
            {
                Code = procedure.Code,
                Name = procedure.Name,
                Modality = procedure.Modality,
                BodyPart = procedure.BodyPart,
                ServiceItemId = services.TryGetValue(procedure.Code, out var serviceItemId) ? serviceItemId : null,
                DurationMinutes = procedure.DurationMinutes,
                RequiresContrast = procedure.RequiresContrast,
                PatientPreparation = procedure.Preparation,
                RequiresFormF = procedure.RequiresFormF,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedReportTemplatesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<ReportTemplate>().IgnoreQueryFilters().Select(t => t.Code).ToListAsync(ct);
        foreach (var template in RadiologyTemplates.All.Where(t => !existing.Contains(t.Code)))
        {
            db.Add(new ReportTemplate
            {
                Code = template.Code,
                Name = template.Name,
                Modality = template.Modality,
                BodyPart = template.BodyPart,
                Technique = template.Technique,
                Findings = template.Findings,
                Impression = template.Impression,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Templates this module adds to the platform defaults (lab.report_ready by e-mail, critical imaging/cardiology finding SMS).</summary>
    private static async Task SeedNotificationTemplatesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<NotificationTemplate>().IgnoreQueryFilters()
            .Where(t => t.FacilityId == null && t.Language == "en")
            .Select(t => new { t.Key, t.Channel })
            .ToListAsync(ct);
        foreach (var template in Templates.Where(t => !existing.Any(e => e.Key == t.Key && e.Channel == t.Channel)))
        {
            db.Add(new NotificationTemplate
            {
                Key = template.Key,
                Channel = template.Channel,
                Subject = template.Subject,
                Body = template.Body,
                Description = template.Description,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private sealed record ServiceSeed(string Code, string Name, string ShortName, ServiceCategory Category, decimal Price, string Department);
}
