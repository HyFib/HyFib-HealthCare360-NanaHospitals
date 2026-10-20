using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;

/// <summary>
/// /api/v1/lab/tests, /instruments, /referral-labs: the test master with panels and reference intervals (LIS-01),
/// analysers connected through the equipment gateway (INT-LAB) and reference laboratories (LIS-08).
/// </summary>
internal static class LabMasterEndpoints
{
    private static readonly Dictionary<string, Expression<Func<LabTest, object?>>> TestSort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code"] = t => t.Code,
        ["name"] = t => t.Name,
        ["section"] = t => t.Section,
        ["tatHours"] = t => t.TatHours,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        string[] readers = [DiagnosticsPermissions.LabOrdersRead, DiagnosticsPermissions.LabOrdersCreate, DiagnosticsPermissions.LabMastersManage];

        var tests = app.MapGroup("/api/v1/lab/tests").WithTags("Laboratory masters");
        tests.MapGet("/", ListTests).RequireAnyPermission(readers);
        tests.MapGet("/{id:guid}", GetTest).RequireAnyPermission(readers);
        tests.MapPost("/", (SaveLabTestRequest request, HospitalDbContext db, CancellationToken ct) => SaveTest(null, request, db, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabTestRequest>();
        tests.MapPut("/{id:guid}", (Guid id, SaveLabTestRequest request, HospitalDbContext db, CancellationToken ct) => SaveTest(id, request, db, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabTestRequest>();
        tests.MapPut("/{id:guid}/components", SetComponents).RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SetComponentsRequest>();
        tests.MapPut("/{id:guid}/ranges", SetRanges).RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SetRangesRequest>();

        var instruments = app.MapGroup("/api/v1/lab/instruments").WithTags("Laboratory masters");
        instruments.MapGet("/", ListInstruments).RequireAnyPermission(DiagnosticsPermissions.LabMastersManage, DiagnosticsPermissions.LabQcManage);
        instruments.MapPost("/", (SaveLabInstrumentRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct) => SaveInstrument(null, request, db, user, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabInstrumentRequest>();
        instruments.MapPut("/{id:guid}", (Guid id, SaveLabInstrumentRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct) => SaveInstrument(id, request, db, user, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabInstrumentRequest>();

        var referralLabs = app.MapGroup("/api/v1/lab/referral-labs").WithTags("Laboratory masters");
        referralLabs.MapGet("/", ListReferralLabs)
            .RequireAnyPermission(DiagnosticsPermissions.LabMastersManage, DiagnosticsPermissions.LabSamplesReceive, DiagnosticsPermissions.LabOrdersRead);
        referralLabs.MapPost("/", (SaveReferralLabRequest request, HospitalDbContext db, CancellationToken ct) => SaveReferralLab(null, request, db, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveReferralLabRequest>();
        referralLabs.MapPut("/{id:guid}", (Guid id, SaveReferralLabRequest request, HospitalDbContext db, CancellationToken ct) => SaveReferralLab(id, request, db, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveReferralLabRequest>();
    }

    // ------------------------------------------------------------------ tests

    private static async Task<IResult> ListTests(
        [AsParameters] PageQuery query, LabSection? section, bool? orderable, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var tests = db.Set<LabTest>().AsNoTracking();
        if (includeInactive != true)
        {
            tests = tests.Where(t => t.IsActive);
        }

        if (section is not null)
        {
            tests = tests.Where(t => t.Section == section);
        }

        if (orderable is { } isOrderable)
        {
            tests = tests.Where(t => t.IsOrderable == isOrderable);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            tests = tests.Where(t => EF.Functions.ILike(t.Name, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(t.Code, like, QueryExtensions.LikeEscape) || (t.ShortName != null && EF.Functions.ILike(t.ShortName, like, QueryExtensions.LikeEscape)));
        }

        var result = await tests.OrderByKey(page, TestSort, t => t.Name)
            .Select(t => new
            {
                t.Id,
                t.Code,
                t.Name,
                t.ShortName,
                t.Section,
                t.SpecimenType,
                t.Container,
                t.ResultType,
                t.Unit,
                t.IsPanel,
                t.IsOrderable,
                t.IsProvisional,
                t.IsActive,
                t.TatHours,
                t.ServiceItemId,
                t.Preparation,
                Price = db.Set<ServiceItem>().Where(s => s.Id == t.ServiceItemId).Select(s => (decimal?)s.BasePrice).FirstOrDefault(),
                Components = t.Components.Count,
            })
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(t => new LabTestListItem(
            t.Id, t.Code, t.Name, t.ShortName, t.Section, t.SpecimenType, t.Container, t.ResultType, t.Unit, t.IsPanel, t.IsOrderable, t.IsProvisional,
            t.IsActive, t.TatHours, t.ServiceItemId, t.Price, t.Preparation, t.Components)));
    }

    private static async Task<IResult> GetTest(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var test = await db.Set<LabTest>().AsNoTracking().Include(t => t.Components).Include(t => t.Ranges).FirstOrDefaultAsync(t => t.Id == id, ct);
        return test is null ? Error.NotFound(nameof(LabTest), id).ToProblem() : TypedResults.Ok(await ToDetailAsync(test, db, ct));
    }

    private static async Task<IResult> SaveTest(Guid? id, SaveLabTestRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim();
        LabTest test;
        if (id is { } existingId)
        {
            var found = await db.Set<LabTest>().FirstOrDefaultAsync(t => t.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(LabTest), existingId).ToProblem();
            }

            test = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(test, version);
            }
        }
        else
        {
            test = new LabTest();
            db.Add(test);
        }

        if (!string.Equals(test.Code, code, StringComparison.Ordinal)
            && await db.Set<LabTest>().IgnoreQueryFilters().AnyAsync(t => t.Code == code && t.Id != test.Id, ct))
        {
            return Error.Conflict("lab.test_code_exists", $"A test with code {code} already exists.").ToProblem();
        }

        if (request.ServiceItemId is { } serviceItemId)
        {
            if (!await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceItemId, ct))
            {
                return Error.Field("serviceItemId", "Choose an existing billable service.").ToProblem();
            }

            if (await db.Set<LabTest>().AnyAsync(t => t.ServiceItemId == serviceItemId && t.Id != test.Id && t.IsActive, ct))
            {
                return Error.Conflict("lab.service_linked", "This billable service is already linked to another active test.").ToProblem();
            }
        }

        if (request.DefaultReferralLabId is { } labId && !await db.Set<ReferralLab>().AnyAsync(l => l.Id == labId && l.IsActive, ct))
        {
            return Error.Field("defaultReferralLabId", "Choose an active reference laboratory.").ToProblem();
        }

        test.Code = code;
        test.Name = request.Name.Trim();
        test.ShortName = Clean(request.ShortName);
        test.Section = request.Section;
        test.SpecimenType = request.SpecimenType;
        test.Container = request.Container;
        test.Method = Clean(request.Method);
        test.TatHours = request.TatHours;
        test.Loinc = Clean(request.Loinc);
        test.LoincName = test.Loinc is null ? null : Clean(request.LoincName);
        test.ServiceItemId = request.ServiceItemId;
        test.IsOrderable = request.IsOrderable;
        test.IsPanel = request.IsPanel;
        test.ResultType = request.ResultType;
        test.Unit = Clean(request.Unit);
        test.Decimals = request.Decimals;
        test.Options = (request.Options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        test.InstrumentChannelCodes = (request.InstrumentChannelCodes ?? []).Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length > 0).Distinct().ToList();
        test.QcGroup = Clean(request.QcGroup)?.ToUpperInvariant();
        test.DeltaThresholdPercent = request.DeltaThresholdPercent;
        test.AllowAutoValidation = request.AllowAutoValidation;
        test.Preparation = Clean(request.Preparation);
        test.DrawSeparately = request.DrawSeparately;
        test.Interpretation = Clean(request.Interpretation);
        test.DefaultReferralLabId = request.DefaultReferralLabId;
        test.DisplayOrder = request.DisplayOrder;
        test.IsActive = request.IsActive;

        // Saving from the masters screen completes the set-up of a test created automatically from an unmapped order.
        test.IsProvisional = false;

        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/lab/tests/{test.Id}", new { test.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> SetComponents(Guid id, SetComponentsRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var panel = await db.Set<LabTest>().Include(t => t.Components).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (panel is null)
        {
            return Error.NotFound(nameof(LabTest), id).ToProblem();
        }

        if (!panel.IsPanel)
        {
            return Error.Rule("lab.not_panel", "Mark the test as a panel before adding components.").ToProblem();
        }

        var ids = request.Components.Select(c => c.TestId).ToList();
        if (ids.Contains(id) || await ContainsPanelAsync(db, ids, id, ct))
        {
            return Error.Field("components", "A panel cannot contain itself, directly or through another panel.").ToProblem();
        }

        if (await db.Set<LabTest>().CountAsync(t => ids.Contains(t.Id), ct) != ids.Count)
        {
            return Error.Field("components", "Some of the analytes no longer exist.").ToProblem();
        }

        // Children change, so the panel row is touched to apply the optimistic-concurrency check on the panel.
        db.SetExpectedVersion(panel, request.Version);
        db.Entry(panel).Property(t => t.UpdatedAt).IsModified = true;

        foreach (var old in panel.Components.ToList())
        {
            db.Remove(old);
        }

        var order = 0;
        foreach (var component in request.Components)
        {
            db.Add(new LabTestComponent
            {
                PanelTestId = panel.Id,
                TestId = component.TestId,
                DisplayOrder = ++order,
                GroupName = Clean(component.GroupName),
            });
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> SetRanges(Guid id, SetRangesRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var test = await db.Set<LabTest>().Include(t => t.Ranges).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (test is null)
        {
            return Error.NotFound(nameof(LabTest), id).ToProblem();
        }

        var ranges = request.Ranges.Select(r => new ReferenceRange
        {
            LabTestId = test.Id,
            Gender = r.Gender is Gender.Male or Gender.Female ? r.Gender : null,
            AgeFromDays = r.AgeFromDays,
            AgeToDays = r.AgeToDays,
            Low = r.Low,
            High = r.High,
            CriticalLow = r.CriticalLow,
            CriticalHigh = r.CriticalHigh,
            Text = Clean(r.Text),
            NormalValues = (r.NormalValues ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList(),
            CriticalValues = (r.CriticalValues ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList(),
        }).ToList();

        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var valid = ranges[i].Validate();
            if (valid.IsFailure)
            {
                errors[$"ranges[{i}]"] = [valid.Error!.Message];
            }
            else if (test.ResultType == LabResultType.Options && test.Options.Count > 0
                     && ranges[i].NormalValues.Concat(ranges[i].CriticalValues).Any(v => !test.Options.Contains(v, StringComparer.OrdinalIgnoreCase)))
            {
                errors[$"ranges[{i}]"] = ["Normal and critical answers must be among the test's options."];
            }
        }

        if (errors.Count > 0)
        {
            return Error.Validation("range.invalid", "Some reference intervals are invalid.", errors).ToProblem();
        }

        db.SetExpectedVersion(test, request.Version);
        db.Entry(test).Property(t => t.UpdatedAt).IsModified = true;
        foreach (var old in test.Ranges.ToList())
        {
            db.Remove(old);
        }

        foreach (var range in ranges)
        {
            db.Add(range);
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>True when one of the proposed components is (or contains, up to three levels) the panel itself.</summary>
    private static async Task<bool> ContainsPanelAsync(HospitalDbContext db, IReadOnlyCollection<Guid> componentIds, Guid panelId, CancellationToken ct)
    {
        var frontier = componentIds.ToList();
        for (var depth = 0; depth < 3 && frontier.Count > 0; depth++)
        {
            var batch = frontier;
            frontier = await db.Set<LabTestComponent>().AsNoTracking().Where(c => batch.Contains(c.PanelTestId)).Select(c => c.TestId).ToListAsync(ct);
            if (frontier.Contains(panelId))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<LabTestDetail> ToDetailAsync(LabTest t, HospitalDbContext db, CancellationToken ct)
    {
        var componentIds = t.Components.Select(c => c.TestId).ToList();
        var names = await db.Set<LabTest>().AsNoTracking().Where(x => componentIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Code, x.Name }).ToDictionaryAsync(x => x.Id, ct);
        return new LabTestDetail(
            t.Id, t.Code, t.Name, t.ShortName, t.Section, t.SpecimenType, t.Container, t.Method, t.TatHours, t.Loinc, t.LoincName, t.ServiceItemId, t.IsOrderable,
            t.IsPanel, t.ResultType, t.Unit, t.Decimals, t.Options, t.InstrumentChannelCodes, t.QcGroup, t.DeltaThresholdPercent, t.AllowAutoValidation,
            t.Preparation, t.DrawSeparately, t.Interpretation, t.DefaultReferralLabId, t.DisplayOrder, t.IsProvisional, t.IsActive,
            t.Components.OrderBy(c => c.DisplayOrder).Select(c => new LabTestComponentDto(
                c.TestId, names.GetValueOrDefault(c.TestId)?.Code ?? string.Empty, names.GetValueOrDefault(c.TestId)?.Name ?? string.Empty, c.DisplayOrder, c.GroupName)).ToList(),
            t.Ranges.OrderBy(r => r.Gender).ThenBy(r => r.AgeFromDays).Select(r => new ReferenceRangeDto(
                r.Id, r.Gender, r.AgeFromDays, r.AgeToDays, r.Low, r.High, r.CriticalLow, r.CriticalHigh, r.Text, r.NormalValues, r.CriticalValues)).ToList(),
            t.Version);
    }

    // ------------------------------------------------------------------ analysers

    private static async Task<IResult> ListInstruments(bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<LabInstrument>().AsNoTracking()
            .Where(i => includeInactive == true || i.IsActive)
            .OrderBy(i => i.Section).ThenBy(i => i.Name)
            .Select(i => new LabInstrumentDto(
                i.Id, i.Code, i.Name, i.Manufacturer, i.Model, i.Section, i.Protocol, i.IsBidirectional, i.AutoValidationEnabled, i.Location, i.IsActive, i.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveInstrument(Guid? id, SaveLabInstrumentRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        LabInstrument instrument;
        if (id is { } existingId)
        {
            var found = await db.Set<LabInstrument>().FirstOrDefaultAsync(i => i.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(LabInstrument), existingId).ToProblem();
            }

            instrument = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(instrument, version);
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            instrument = new LabInstrument { FacilityId = facilityId };
            db.Add(instrument);
        }

        // Codes identify analysers across facilities (the gateway sends only the code).
        if (!string.Equals(instrument.Code, code, StringComparison.Ordinal)
            && await db.Set<LabInstrument>().IgnoreQueryFilters().AnyAsync(i => i.Code == code && i.Id != instrument.Id, ct))
        {
            return Error.Conflict("lab.instrument_code_exists", $"An analyser with code {code} already exists.").ToProblem();
        }

        instrument.Code = code;
        instrument.Name = request.Name.Trim();
        instrument.Manufacturer = Clean(request.Manufacturer);
        instrument.Model = Clean(request.Model);
        instrument.Section = request.Section;
        instrument.Protocol = request.Protocol;
        instrument.IsBidirectional = request.IsBidirectional;
        instrument.AutoValidationEnabled = request.AutoValidationEnabled;
        instrument.Location = Clean(request.Location);
        instrument.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/lab/instruments/{instrument.Id}", new { instrument.Id }) : TypedResults.NoContent();
    }

    // ------------------------------------------------------------------ reference laboratories

    private static async Task<IResult> ListReferralLabs(bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<ReferralLab>().AsNoTracking()
            .Where(l => includeInactive == true || l.IsActive)
            .OrderBy(l => l.Name)
            .Select(l => new ReferralLabDto(l.Id, l.Code, l.Name, l.ContactPerson, l.Phone, l.Email, l.Address, l.AccreditationNumber, l.TatDays, l.IsActive, l.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveReferralLab(Guid? id, SaveReferralLabRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        ReferralLab lab;
        if (id is { } existingId)
        {
            var found = await db.Set<ReferralLab>().FirstOrDefaultAsync(l => l.Id == existingId, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(ReferralLab), existingId).ToProblem();
            }

            lab = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(lab, version);
            }
        }
        else
        {
            lab = new ReferralLab();
            db.Add(lab);
        }

        if (!string.Equals(lab.Code, code, StringComparison.Ordinal)
            && await db.Set<ReferralLab>().IgnoreQueryFilters().AnyAsync(l => l.Code == code && l.Id != lab.Id, ct))
        {
            return Error.Conflict("lab.referral_code_exists", $"A reference laboratory with code {code} already exists.").ToProblem();
        }

        lab.Code = code;
        lab.Name = request.Name.Trim();
        lab.ContactPerson = Clean(request.ContactPerson);
        lab.Phone = Phone.Normalize(request.Phone);
        lab.Email = Clean(request.Email);
        lab.Address = Clean(request.Address);
        lab.AccreditationNumber = Clean(request.AccreditationNumber);
        lab.TatDays = request.TatDays;
        lab.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/lab/referral-labs/{lab.Id}", new { lab.Id }) : TypedResults.NoContent();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
