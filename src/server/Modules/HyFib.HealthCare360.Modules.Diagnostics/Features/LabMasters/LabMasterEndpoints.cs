using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;

/// <summary>
/// /api/v1/lab/tests, /instruments, /referral-labs: the test master with panels and reference intervals (LIS-01),
/// analysers connected through the equipment gateway (INT-LAB), the channels they send that no test maps, and reference
/// laboratories (LIS-08).
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
        tests.MapPost("/", (SaveLabTestRequest request, HospitalDbContext db, LabSafetyLimits safety, CancellationToken ct) => SaveTest(null, request, db, safety, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabTestRequest>();
        tests.MapPut("/{id:guid}", (Guid id, SaveLabTestRequest request, HospitalDbContext db, LabSafetyLimits safety, CancellationToken ct) => SaveTest(id, request, db, safety, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabTestRequest>().RequireVersion();
        tests.MapPut("/{id:guid}/components", SetComponents).RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SetComponentsRequest>();
        tests.MapPut("/{id:guid}/ranges", SetRanges).RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SetRangesRequest>();

        var instruments = app.MapGroup("/api/v1/lab/instruments").WithTags("Laboratory masters");
        instruments.MapGet("/", ListInstruments).RequireAnyPermission(DiagnosticsPermissions.LabMastersManage, DiagnosticsPermissions.LabQcManage);
        instruments.MapPost("/", (SaveLabInstrumentRequest request, HospitalDbContext db, ICurrentUser user, LabSafetyLimits safety, CancellationToken ct) => SaveInstrument(null, request, db, user, safety, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabInstrumentRequest>();
        instruments.MapPut("/{id:guid}", (Guid id, SaveLabInstrumentRequest request, HospitalDbContext db, ICurrentUser user, LabSafetyLimits safety, CancellationToken ct) => SaveInstrument(id, request, db, user, safety, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveLabInstrumentRequest>().RequireVersion();

        var channels = app.MapGroup("/api/v1/lab/instruments/channels").WithTags("Laboratory masters");
        channels.MapGet("/", ListChannels).RequireAnyPermission(DiagnosticsPermissions.LabMastersManage, DiagnosticsPermissions.LabQcManage);
        channels.MapPost("/{id:guid}/ignore", IgnoreChannel)
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<IgnoreAnalyserChannelRequest>();
        channels.MapDelete("/{id:guid}/ignore", StopIgnoringChannel).RequirePermission(DiagnosticsPermissions.LabMastersManage);

        var referralLabs = app.MapGroup("/api/v1/lab/referral-labs").WithTags("Laboratory masters");
        referralLabs.MapGet("/", ListReferralLabs)
            .RequireAnyPermission(DiagnosticsPermissions.LabMastersManage, DiagnosticsPermissions.LabSamplesReceive, DiagnosticsPermissions.LabOrdersRead);
        referralLabs.MapPost("/", (SaveReferralLabRequest request, HospitalDbContext db, CancellationToken ct) => SaveReferralLab(null, request, db, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveReferralLabRequest>();
        referralLabs.MapPut("/{id:guid}", (Guid id, SaveReferralLabRequest request, HospitalDbContext db, CancellationToken ct) => SaveReferralLab(id, request, db, ct))
            .RequirePermission(DiagnosticsPermissions.LabMastersManage).WithValidation<SaveReferralLabRequest>().RequireVersion();
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

    /// <summary>
    /// 201/204 when saved; 202 with the request when automatic verification is switched on and the lab asks for a second
    /// pathologist: the rest is saved, and automatic verification stays off until another pathologist approves it.
    /// </summary>
    private static async Task<IResult> SaveTest(Guid? id, SaveLabTestRequest request, HospitalDbContext db, LabSafetyLimits safety, CancellationToken ct)
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

        // A LOINC name is the name LOINC gives its code (ABDM health records carry the code with it). A new code sent with
        // the old code's name would put that name on the wrong code: the form clears the name when the code changes, and
        // a request that does not is refused.
        var loinc = Clean(request.Loinc);
        var loincName = loinc is null ? null : Clean(request.LoincName);
        if (id is not null && loinc != test.Loinc && loincName is not null && string.Equals(loincName, test.LoincName, StringComparison.Ordinal))
        {
            return Error.Field(
                "loincName",
                "The LOINC code changed: enter the name LOINC gives the new code. If the name on file is already the new code's (the code was mistyped), save the code without a name first, then add the name.")
                .ToProblem();
        }

        test.Code = code;
        test.Name = request.Name.Trim();
        test.ShortName = Clean(request.ShortName);
        test.Section = request.Section;
        test.SpecimenType = request.SpecimenType;
        test.Container = request.Container;
        test.Method = Clean(request.Method);
        test.TatHours = request.TatHours;
        test.Loinc = loinc;
        test.LoincName = loincName;
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
        var autoValidationAsked = request.AllowAutoValidation && (id is null || !test.AllowAutoValidation)
                                  && await safety.GuardedForTestsAsync(ct);
        if (autoValidationAsked && string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Field("reason", "Say why automatic verification is switched on: it waits for a second pathologist, who reads it.").ToProblem();
        }

        test.AllowAutoValidation = autoValidationAsked ? (id is not null && test.AllowAutoValidation) : request.AllowAutoValidation;
        test.Preparation = Clean(request.Preparation);
        test.DrawSeparately = request.DrawSeparately;
        test.Interpretation = Clean(request.Interpretation);
        test.DefaultReferralLabId = request.DefaultReferralLabId;
        test.DisplayOrder = request.DisplayOrder;
        test.IsActive = request.IsActive;

        // Saving from the masters screen completes the set-up of a test created automatically from an unmapped order.
        test.IsProvisional = false;

        await db.SaveChangesAsync(ct);
        if (autoValidationAsked)
        {
            var asked = await safety.AskAsync(
                new LabSafetyPayload(LabSafetyTarget.TestAutoValidation, test.Id, null), nameof(LabTest), test.Version, null,
                $"Switch on automatic verification of {test.Name}",
                [new("Test", $"{test.Name} ({test.Code})"), new("Automatic verification", "off → on: analyser results within limits are released with nobody looking at them")],
                request.Reason, ct);
            return asked.IsFailure ? asked.Error!.ToProblem() : TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);
        }

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

    /// <summary>
    /// 204 when saved; 202 with the request when a critical limit is loosened (lowered, raised or taken away, or a critical
    /// answer no longer critical, for any patient) and the lab asks for a second pathologist: nothing changes until another
    /// pathologist approves it. Narrowing or adding critical limits is saved at once.
    /// </summary>
    private static async Task<IResult> SetRanges(Guid id, SetRangesRequest request, HospitalDbContext db, LabSafetyLimits safety, CancellationToken ct)
    {
        var test = await db.Set<LabTest>().Include(t => t.Ranges).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (test is null)
        {
            return Error.NotFound(nameof(LabTest), id).ToProblem();
        }

        var built = BuildRanges(test, request);
        if (built.IsFailure)
        {
            return built.Error!.ToProblem();
        }

        var loosened = CriticalLimitChanges.Loosened(test.Ranges, built.Value, test.Decimals);
        if (loosened.Count > 0 && await safety.GuardedForTestsAsync(ct))
        {
            if (test.Version != request.Version)
            {
                return Error.Conflict("concurrency", "The test changed since you opened it. Reload it and try again.").ToProblem();
            }

            var asked = await safety.AskAsync(
                new LabSafetyPayload(LabSafetyTarget.TestRanges, test.Id, request), nameof(LabTest), test.Version, null,
                $"Loosen the critical limits of {test.Name}",
                [new("Test", $"{test.Name} ({test.Code})"), .. loosened.Select(l => new ApprovalDetail("Critical limit", l))],
                request.Reason, ct);
            return asked.IsFailure ? asked.Error!.ToProblem() : TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);
        }

        db.SetExpectedVersion(test, request.Version);
        ReplaceRanges(db, test, built.Value);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The intervals asked for, checked against each other and the test's options.</summary>
    internal static Result<IReadOnlyList<ReferenceRange>> BuildRanges(LabTest test, SetRangesRequest request)
    {
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
            return Error.Validation("range.invalid", "Some reference intervals are invalid.", errors);
        }

        return Result<IReadOnlyList<ReferenceRange>>.Ok(ranges);
    }

    /// <summary>Replaces the test's intervals (the test row is touched, so its version guards the change); does not save.</summary>
    internal static void ReplaceRanges(HospitalDbContext db, LabTest test, IReadOnlyList<ReferenceRange> ranges)
    {
        db.Entry(test).Property(t => t.UpdatedAt).IsModified = true;
        foreach (var old in test.Ranges.ToList())
        {
            db.Remove(old);
        }

        foreach (var range in ranges)
        {
            db.Add(range);
        }
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

    // ------------------------------------------------------------------ channels no test maps

    /// <summary>
    /// The channels the facility's analysers in use send that no active test maps now: a channel mapped since it was
    /// sent is left out, so mapping it in a test is all it takes. Each with how to write its mapping, and the lab's
    /// decision to ignore it where there is one.
    /// </summary>
    private static async Task<IResult> ListChannels(HospitalDbContext db, DiagnosticsLookups lookups, CancellationToken ct)
    {
        var instruments = await db.Set<LabInstrument>().AsNoTracking()
            .Where(i => i.IsActive)
            .ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        var ids = instruments.Keys.ToList();
        var sent = await db.Set<AnalyserChannel>().AsNoTracking().Where(c => ids.Contains(c.InstrumentId)).ToListAsync(ct);
        var catalogue = await MappingsAsync(db, ct);
        var ignored = await db.Set<IgnoredAnalyserChannel>().AsNoTracking().Where(i => ids.Contains(i.InstrumentId)).ToListAsync(ct);
        var names = await lookups.UserNamesAsync(ignored.Select(i => i.CreatedBy), ct);

        return TypedResults.Ok(sent
            .Where(c => !ChannelMappings.MapsAny(catalogue, instruments[c.InstrumentId], c.ChannelCode))
            .OrderBy(c => instruments[c.InstrumentId], StringComparer.Ordinal)
            .ThenBy(c => c.ChannelCode, StringComparer.Ordinal)
            .Select(c =>
            {
                var code = instruments[c.InstrumentId];
                var decision = ignored.FirstOrDefault(i => i.InstrumentId == c.InstrumentId && i.ChannelCode == c.ChannelCode);
                return new AnalyserChannelDto(
                    c.Id, c.InstrumentId, code, c.ChannelCode, c.FirstSeenAt, c.LastSeenAt, c.TimesSeen, c.LastValue, c.LastUnit,
                    MappingOf(code, c.ChannelCode),
                    decision is null
                        ? null
                        : new IgnoredAnalyserChannelDto(decision.Reason, decision.CreatedBy is { } by ? names.GetValueOrDefault(by) : null, decision.CreatedAt));
            })
            .ToList());
    }

    /// <summary>
    /// The lab decides a channel goes on no report: a research parameter, or a flag the analyser sends as a channel. It
    /// is then listed as ignored, not waiting to be mapped. A channel a test maps now has nothing to ignore.
    /// </summary>
    private static async Task<IResult> IgnoreChannel(Guid id, IgnoreAnalyserChannelRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var channel = await db.Set<AnalyserChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        var instrument = channel is null ? null : await db.Set<LabInstrument>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == channel.InstrumentId, ct);
        if (channel is null || instrument is null)
        {
            return Error.NotFound(nameof(AnalyserChannel), id).ToProblem();
        }

        if (ChannelMappings.MapsAny(await MappingsAsync(db, ct), instrument.Code, channel.ChannelCode))
        {
            return Error.Rule("lab.channel_mapped", $"A test maps {instrument.Code}'s channel {channel.ChannelCode} now, so there is nothing to ignore.").ToProblem();
        }

        if (await db.Set<IgnoredAnalyserChannel>().AnyAsync(i => i.InstrumentId == channel.InstrumentId && i.ChannelCode == channel.ChannelCode, ct))
        {
            return Error.Conflict("lab.channel_ignored", $"{instrument.Code}'s channel {channel.ChannelCode} is ignored already.").ToProblem();
        }

        db.Add(new IgnoredAnalyserChannel
        {
            FacilityId = channel.FacilityId,
            InstrumentId = channel.InstrumentId,
            ChannelCode = channel.ChannelCode,
            Reason = request.Reason.Trim(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The lab takes its decision back: the channel waits to be mapped again.</summary>
    private static async Task<IResult> StopIgnoringChannel(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var channel = await db.Set<AnalyserChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        var decision = channel is null
            ? null
            : await db.Set<IgnoredAnalyserChannel>().FirstOrDefaultAsync(i => i.InstrumentId == channel.InstrumentId && i.ChannelCode == channel.ChannelCode, ct);
        if (decision is null)
        {
            return Error.NotFound(nameof(IgnoredAnalyserChannel), id).ToProblem();
        }

        db.Remove(decision);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>The channel codes of every active test, one list per test.</summary>
    private static Task<List<List<string>>> MappingsAsync(HospitalDbContext db, CancellationToken ct) =>
        db.Set<LabTest>().AsNoTracking()
            .Where(t => t.IsActive && t.InstrumentChannelCodes.Count > 0)
            .Select(t => t.InstrumentChannelCodes)
            .ToListAsync(ct);

    /// <summary>
    /// The analyser's own mapping when it can be written, else the plain code, else null. A channel with ':' in it
    /// cannot be mapped at all: a mapping reads what is before the ':' as the analyser.
    /// </summary>
    internal static string? MappingOf(string instrumentCode, string channelCode)
    {
        if (channelCode.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        var own = $"{instrumentCode}:{channelCode}";
        return MasterRules.IsChannelMapping(own) ? own : MasterRules.IsChannelMapping(channelCode) ? channelCode : null;
    }

    /// <summary>
    /// 201/204 when saved; 202 with the request when automatic verification is switched on and the analyser's facility asks
    /// for a second pathologist: the rest is saved, and automatic verification stays off until another pathologist approves.
    /// </summary>
    private static async Task<IResult> SaveInstrument(
        Guid? id, SaveLabInstrumentRequest request, HospitalDbContext db, ICurrentUser user, LabSafetyLimits safety, CancellationToken ct)
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
        var autoValidationAsked = request.AutoValidationEnabled && (id is null || !instrument.AutoValidationEnabled)
                                  && await safety.GuardedForFacilityAsync(instrument.FacilityId, ct);
        if (autoValidationAsked && string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Field("reason", "Say why automatic verification is switched on: it waits for a second pathologist, who reads it.").ToProblem();
        }

        instrument.AutoValidationEnabled = autoValidationAsked ? (id is not null && instrument.AutoValidationEnabled) : request.AutoValidationEnabled;
        instrument.Location = Clean(request.Location);
        instrument.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        if (autoValidationAsked)
        {
            var asked = await safety.AskAsync(
                new LabSafetyPayload(LabSafetyTarget.InstrumentAutoValidation, instrument.Id, null), nameof(LabInstrument), instrument.Version,
                instrument.FacilityId, $"Switch on automatic verification of analyser {instrument.Code}",
                [new("Analyser", $"{instrument.Name} ({instrument.Code})"), new("Automatic verification", "off → on: its results within limits are released with nobody looking at them")],
                request.Reason, ct);
            return asked.IsFailure ? asked.Error!.ToProblem() : TypedResults.Accepted($"/api/v1/approvals/{asked.Value.Id}", asked.Value);
        }

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
