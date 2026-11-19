using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>
/// Diagnostics reference data: billable services (<c>LAB-*</c>, <c>RAD-*</c>, <c>CARD-*</c>), the laboratory test master with
/// panels, reference intervals and critical limits, imaging procedures, radiology report templates, and the extra
/// notification templates this module sends. It runs on every start and only inserts what is missing (by code).
/// Rows the laboratory or radiology have edited are never overwritten. A database seeded before is brought up to date
/// where the module changed since: the CBC's parameters, analyser channel codes, and tube numbers ending in a check digit.
/// </summary>
internal sealed class DiagnosticsSeeder(IAuditLogger audit, TimeProvider time) : ISeeder
{
    /// <summary>The audit action of tube numbers brought to their check digit.</summary>
    public const string TubeNumbersUpgradedAction = "numbering.tube_check_digit";

    private static readonly JsonSerializerOptions SettingJson = new(JsonSerializerDefaults.Web);

    /// <summary>GST SAC 999316: medical laboratory and diagnostic-imaging services (exempt healthcare).</summary>
    public const string SacCode = "999316";

    /// <summary>
    /// The templates this module adds to the platform defaults, each in its language: the "report ready" e-mail, the
    /// critical finding SMS, and the notice of a corrected report (<see cref="ReportDelivery.ReportAmendedTemplate"/>) on
    /// each channel of the "report ready" one, in English, Hindi and Tamil.
    /// </summary>
    internal static readonly (string Key, NotificationChannel Channel, string Language, string? Subject, string Body, string Description)[] Templates =
    [
        (ReportDelivery.ReportReadyTemplate, NotificationChannel.Email, "en", "Your report {{reference}} is ready",
            "<p>Dear {{name}},</p><p>Your diagnostic report <strong>{{reference}}</strong> is ready. A copy is attached, and you can also view and download it securely on the patient portal.</p><p>Regards,<br/>{{hospital}}</p>",
            "Diagnostic report ready (e-mail)"),
        (CriticalValueNotifier.FindingCriticalTemplate, NotificationChannel.Sms, "en", null,
            "HC360 CRITICAL FINDING: {{patient}} ({{uhid}}) {{study}}: {{finding}}. Please review immediately.",
            "Critical imaging/cardiology finding to the ordering doctor"),

        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.Sms, "en", null,
            "Dear {{name}}, your report {{reference}} has been corrected. Please use the corrected report on the patient portal, not the earlier one. - {{hospital}}",
            "Lab report corrected after release"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.WhatsApp, "en", null,
            "Dear {{name}}, your report {{reference}} has been corrected. Please use the corrected report, not the earlier one. It is also on the patient portal.",
            "Lab report corrected after release (WhatsApp)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.Email, "en", "Your report {{reference}} has been corrected",
            "<p>Dear {{name}},</p><p>Your diagnostic report <strong>{{reference}}</strong> has been corrected. The corrected report is attached, and you can also view and download it securely on the patient portal. Please use it, and not the earlier one.</p><p>Regards,<br/>{{hospital}}</p>",
            "Lab report corrected after release (e-mail)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.Sms, "hi", null,
            "प्रिय {{name}}, आपकी रिपोर्ट {{reference}} में सुधार किया गया है। कृपया रोगी पोर्टल पर सुधारी गई रिपोर्ट का उपयोग करें, पिछली रिपोर्ट का नहीं। - {{hospital}}",
            "Lab report corrected after release (Hindi)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.WhatsApp, "hi", null,
            "प्रिय {{name}}, आपकी रिपोर्ट {{reference}} में सुधार किया गया है। कृपया सुधारी गई रिपोर्ट का उपयोग करें, पिछली रिपोर्ट का नहीं। यह रोगी पोर्टल पर भी उपलब्ध है।",
            "Lab report corrected after release (WhatsApp, Hindi)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.Email, "hi", "आपकी रिपोर्ट {{reference}} में सुधार किया गया है",
            "<p>प्रिय {{name}},</p><p>आपकी जाँच रिपोर्ट <strong>{{reference}}</strong> में सुधार किया गया है। सुधारी गई रिपोर्ट संलग्न है, और आप इसे रोगी पोर्टल पर भी सुरक्षित रूप से देख और डाउनलोड कर सकते हैं। कृपया इसी का उपयोग करें, पिछली रिपोर्ट का नहीं।</p><p>सादर,<br/>{{hospital}}</p>",
            "Lab report corrected after release (e-mail, Hindi)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.Sms, "ta", null,
            "அன்புள்ள {{name}}, உங்கள் அறிக்கை {{reference}} திருத்தப்பட்டுள்ளது. நோயாளி போர்ட்டலில் உள்ள திருத்தப்பட்ட அறிக்கையைப் பயன்படுத்துங்கள்; முந்தைய அறிக்கையை அல்ல. - {{hospital}}",
            "Lab report corrected after release (Tamil)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.WhatsApp, "ta", null,
            "அன்புள்ள {{name}}, உங்கள் அறிக்கை {{reference}} திருத்தப்பட்டுள்ளது. திருத்தப்பட்ட அறிக்கையைப் பயன்படுத்துங்கள்; முந்தைய அறிக்கையை அல்ல. இது நோயாளி போர்ட்டலிலும் உள்ளது.",
            "Lab report corrected after release (WhatsApp, Tamil)"),
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.Email, "ta", "உங்கள் அறிக்கை {{reference}} திருத்தப்பட்டுள்ளது",
            "<p>அன்புள்ள {{name}},</p><p>உங்கள் பரிசோதனை அறிக்கை <strong>{{reference}}</strong> திருத்தப்பட்டுள்ளது. திருத்தப்பட்ட அறிக்கை இணைக்கப்பட்டுள்ளது; அதை நோயாளி போர்ட்டலிலும் பாதுகாப்பாகப் பார்த்துப் பதிவிறக்கலாம். முந்தைய அறிக்கைக்குப் பதிலாக இதையே பயன்படுத்துங்கள்.</p><p>அன்புடன்,<br/>{{hospital}}</p>",
            "Lab report corrected after release (e-mail, Tamil)"),
    ];

    /// <summary>
    /// Stored "Not in use" when first seeded: WhatsApp messages Meta must approve first. An administrator switches each on
    /// in Notification templates once its template is approved in WhatsApp Manager; until then the message goes by SMS and
    /// e-mail only.
    /// </summary>
    internal static readonly IReadOnlySet<(string Key, NotificationChannel Channel)> SeededNotInUse = new HashSet<(string, NotificationChannel)>
    {
        (ReportDelivery.ReportAmendedTemplate, NotificationChannel.WhatsApp),
    };

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
        await UpgradeTubeNumbersAsync(db, audit, time.GetUtcNow(), cancellationToken);
    }

    /// <summary>
    /// Tube numbers have 10 digits ending in a check digit ({SEQ:9}{CHK}): Nana Hospitals writes them on the tubes and types
    /// them on the analysers. The series is locked, so the format stored for it is the one it was created with: a database
    /// made before keeps {SEQ:10} (or {SEQ:9}, before that) until it is brought to the definition's here, together with its
    /// counter, counted in tens of the old numbers (<see cref="TubeCounterAfterUpgrade"/>): a counter at 1000012346 goes on
    /// from 100001235, whose number, 1000012350, is above every tube numbered before, and a lower one from the series'
    /// first number (1000000005). When it happened is written to <see cref="TubeNumbers.CheckDigitSinceSetting"/> in the
    /// same transaction, so the tubes numbered before keep being found by their numbers, and it is audited. A database whose
    /// tube numbers have their check digit already, or that never numbered a tube, is left as it is.
    /// </summary>
    internal static async Task UpgradeTubeNumbersAsync(HospitalDbContext db, IAuditLogger audit, DateTimeOffset now, CancellationToken ct)
    {
        var series = DiagnosticsNumberSeries.LabSample;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var row = await db.Set<NumberSeries>().IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Key == series.Key && s.FacilityId == null)
                .Select(s => new { s.Format, s.CurrentValue })
                .FirstOrDefaultAsync(ct);
            if (row is null || string.Equals(row.Format, series.DefaultFormat, StringComparison.Ordinal))
            {
                return;
            }

            // The counter as read, in one statement: a tube numbered meanwhile (by another server) is read again.
            var counter = TubeCounterAfterUpgrade(row.CurrentValue, row.Format);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE platform.number_series
                   SET format = {series.DefaultFormat}, current_value = {counter}, updated_at = {now}
                 WHERE key = {series.Key} AND facility_id IS NULL AND format = {row.Format} AND current_value = {row.CurrentValue}
                """, ct);
            if (changed == 0)
            {
                continue;
            }

            await RecordCheckDigitSinceAsync(db, now, ct);
            var next = NumberFormat.Render(series.DefaultFormat, NumberFormat.NextNumber(counter, series.FirstNumber), null, NumberFormat.ToHospitalTime(now));
            audit.Add(new AuditEntry(AuditCategories.Admin, TubeNumbersUpgradedAction, string.Create(CultureInfo.InvariantCulture,
                $"{series.Name} numbers now end in a check digit: format {row.Format} → {series.DefaultFormat}, next tube {next}"))
            {
                EntityType = nameof(NumberSeries),
                EntityId = series.Key,
                Severity = AuditSeverity.Warning,
                Details = new { series = series.Key, before = new { format = row.Format, value = row.CurrentValue }, after = new { format = series.DefaultFormat, value = counter } },
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
    }

    /// <summary>
    /// The tube counter once tube numbers end in a check digit: counted in tens of the old numbers
    /// (<see cref="NumberFormat.CounterIn"/>), so the next running number, with its check digit, is above every number
    /// issued under <paramref name="format"/>.
    /// </summary>
    internal static long TubeCounterAfterUpgrade(long value, string format) =>
        NumberFormat.CounterIn(value, format, DiagnosticsNumberSeries.LabSample.DefaultFormat);

    /// <summary>Records, for the caller's save, when tube numbers started ending in a check digit (<see cref="TubeNumbers.CheckDigitSinceSetting"/>).</summary>
    private static async Task RecordCheckDigitSinceAsync(HospitalDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // The key is unique, deleted rows too.
        var setting = await db.Set<SystemSetting>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Key == TubeNumbers.CheckDigitSinceSetting && s.FacilityId == null, ct);
        if (setting is null)
        {
            setting = new SystemSetting { Key = TubeNumbers.CheckDigitSinceSetting };
            db.Add(setting);
        }

        setting.Value = JsonSerializer.Serialize<DateTimeOffset?>(now.ToUniversalTime(), SettingJson);
        setting.IsSecret = false;
        setting.IsDeleted = false;
        setting.DeletedAt = null;
        setting.DeletedBy = null;
        setting.Description = "When tube numbers started ending in a check digit. Only a tube numbered before then is found by a number whose check digit does not match.";
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
        if (created.Contains(ChannelsAddedWith))
        {
            await AddChannelsAsync(db, ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Channel codes that tests already in the seed gained after databases were seeded (Nana Hospitals' answers of 17
    /// November 2026): the Yumizen H500's codes for the differential, put first because its host queries ask for a test's
    /// first code, and the names the i-CHROMA and the doctors give vitamin D.
    /// </summary>
    internal static readonly IReadOnlyList<(string Test, string[] Channels, bool First)> ChannelsAdded =
    [
        ("NEUT", ["NEU%"], true), ("LYMPH", ["LYM%"], true), ("MONO", ["MON%"], true), ("EOS", ["EOS%"], true), ("BASO", ["BAS%"], true),
        ("LAB-VITD", ["VIT-D", "VIT D-25", "VIT-D3", "VIT D3", "VIT 25 OH"], false),
    ];

    /// <summary>An analyte that came with <see cref="ChannelsAdded"/>: a run that creates it is upgrading a database from before them.</summary>
    internal const string ChannelsAddedWith = "NEUT-ABS";

    /// <summary>
    /// A database seeded before <see cref="ChannelsAdded"/> gets them in the run that creates the CBC's new parameters,
    /// which happens once, so a code the lab takes off a test later is never put back. A code that a test maps already
    /// (plainly or as an analyser's own) or that the lab chose to ignore is left out, so none of the lab's decisions changes.
    /// </summary>
    private static async Task AddChannelsAsync(HospitalDbContext db, CancellationToken ct)
    {
        var tests = await db.Set<LabTest>().IgnoreQueryFilters().ToListAsync(ct);
        var ignored = await db.Set<IgnoredAnalyserChannel>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Select(c => c.ChannelCode).ToListAsync(ct);
        var taken = tests.SelectMany(t => t.InstrumentChannelCodes).Select(ChannelOf).Concat(ignored).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, channels, first) in ChannelsAdded)
        {
            if (tests.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.Ordinal)) is { } test)
            {
                var upgraded = WithChannels(test.InstrumentChannelCodes, channels, first, taken);
                if (upgraded.Count != test.InstrumentChannelCodes.Count)
                {
                    test.InstrumentChannelCodes = upgraded;
                }
            }
        }
    }

    /// <summary>
    /// A test's channel codes with those of <paramref name="added"/> that are not <paramref name="taken"/> (mapped by a test
    /// or ignored by the lab): before its own when <paramref name="first"/>, else after them.
    /// </summary>
    internal static List<string> WithChannels(IReadOnlyList<string> current, IReadOnlyList<string> added, bool first, IReadOnlySet<string> taken)
    {
        var missing = added.Where(c => !taken.Contains(c)).ToList();
        return first ? [.. missing, .. current] : [.. current, .. missing];
    }

    /// <summary>The channel a mapping names: "NEU%" for both "NEU%" and "H500:NEU%".</summary>
    private static string ChannelOf(string mapping) => mapping[(mapping.IndexOf(':', StringComparison.Ordinal) + 1)..];

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

    /// <summary>
    /// Templates this module adds to the platform defaults (<see cref="Templates"/>), each in its language, for all
    /// facilities: only those missing, so an administrator's wording is never overwritten.
    /// </summary>
    private static async Task SeedNotificationTemplatesAsync(HospitalDbContext db, CancellationToken ct)
    {
        var existing = await db.Set<NotificationTemplate>().IgnoreQueryFilters()
            .Where(t => t.FacilityId == null)
            .Select(t => new { t.Key, t.Channel, t.Language })
            .ToListAsync(ct);
        foreach (var template in Templates.Where(t => !existing.Any(e => e.Key == t.Key && e.Channel == t.Channel && e.Language == t.Language)))
        {
            db.Add(new NotificationTemplate
            {
                Key = template.Key,
                Channel = template.Channel,
                Language = template.Language,
                Subject = template.Subject,
                Body = template.Body,
                Description = template.Description,
                IsActive = !SeededNotInUse.Contains((template.Key, template.Channel)),
            });
        }

        await db.SaveChangesAsync(ct);
    }

    private sealed record ServiceSeed(string Code, string Name, string ShortName, ServiceCategory Category, decimal Price, string Department);
}
