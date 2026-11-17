using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

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
        await UpgradeTubeNumberFormatAsync(db, cancellationToken);
    }

    /// <summary>
    /// Tube numbers have 10 digits (Nana Hospitals writes them on the tubes and types them on the analysers). The series is
    /// locked, so the format stored for it is the one it was created with: a database made before keeps {SEQ:9} until it
    /// is brought to the definition's here. Its counter needs nothing: the series never issues a number below its first
    /// (<c>NumberSeriesDefinition.FirstNumber</c>), so the next tube after the upgrade is 1000000000.
    /// </summary>
    internal static async Task UpgradeTubeNumberFormatAsync(HospitalDbContext db, CancellationToken ct)
    {
        var series = DiagnosticsNumberSeries.LabSample;
        var row = await db.Set<NumberSeries>().IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Key == series.Key && s.FacilityId == null, ct);
        if (row is not null && !string.Equals(row.Format, series.DefaultFormat, StringComparison.Ordinal))
        {
            row.Format = series.DefaultFormat;
            await db.SaveChangesAsync(ct);
        }
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
        var created = new HashSet<string>(StringComparer.Ordinal);
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
            created.Add(test.Code);
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

        await JoinPanelsAsync(db, tests, created, newPanels.Select(p => p.Panel.Code).ToHashSet(StringComparer.Ordinal), ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Analytes new to this database join the seeded panels it already has that list them, in the seed's place: the
    /// CBC's new parameters on a database seeded before them. The laboratory cannot have taken an analyte it never had out
    /// of a panel, so this changes none of its decisions: the panel's own components stay, in their order (those after
    /// the place move down one), and a panel it deleted is left alone.
    /// </summary>
    private static async Task JoinPanelsAsync(
        HospitalDbContext db, IReadOnlyDictionary<string, Guid> tests, IReadOnlySet<string> created, IReadOnlySet<string> newPanels, CancellationToken ct)
    {
        var codesById = tests.ToDictionary(t => t.Value, t => t.Key);
        foreach (var seed in LabCatalog.Orderables.Where(o => o.IsPanel && !newPanels.Contains(o.Code) && o.Components.Any(c => created.Contains(c.Code))))
        {
            if (!tests.TryGetValue(seed.Code, out var panelId)
                || await db.Set<LabTest>().FirstOrDefaultAsync(t => t.Id == panelId, ct) is not { IsPanel: true })
            {
                continue;
            }

            var components = await db.Set<LabTestComponent>().Where(c => c.PanelTestId == panelId).OrderBy(c => c.DisplayOrder).ToListAsync(ct);
            var current = components.Select(c => codesById.GetValueOrDefault(c.TestId) ?? c.TestId.ToString()).ToList();
            var added = seed.Components.Where(c => created.Contains(c.Code)).ToList();
            var order = PanelOrder(current, [.. seed.Components.Select(c => c.Code)], added.Select(c => c.Code));
            foreach (var (component, code) in components.Zip(current))
            {
                var position = order.IndexOf(code) + 1;
                if (component.DisplayOrder != position)
                {
                    component.DisplayOrder = position;
                }
            }

            foreach (var (code, group) in added)
            {
                db.Add(new LabTestComponent { PanelTestId = panelId, TestId = tests[code], DisplayOrder = order.IndexOf(code) + 1, GroupName = group });
            }
        }
    }

    /// <summary>
    /// A panel's analytes (by code) once <paramref name="added"/> join it: each goes after the nearest analyte the seed
    /// lists before it that the panel has, or first when it has none; the panel's own keep their order.
    /// </summary>
    internal static List<string> PanelOrder(IReadOnlyList<string> current, IReadOnlyList<string> seed, IEnumerable<string> added)
    {
        var order = current.ToList();
        foreach (var code in added)
        {
            if (order.Contains(code, StringComparer.Ordinal))
            {
                continue;
            }

            var after = -1;
            for (var i = IndexOf(seed, code) - 1; i >= 0 && after < 0; i--)
            {
                after = order.IndexOf(seed[i]);
            }

            order.Insert(after + 1, code);
        }

        return order;

        static int IndexOf(IReadOnlyList<string> codes, string code)
        {
            for (var i = 0; i < codes.Count; i++)
            {
                if (string.Equals(codes[i], code, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return codes.Count;
        }
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
