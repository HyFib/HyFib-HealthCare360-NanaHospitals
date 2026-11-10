using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Services;

namespace HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;

/// <summary>
/// /api/v1/lab/eqas: external quality assessment (proficiency testing) for NABL 112 and ISO 15189 7.3.7.3. The schemes the
/// lab is enrolled in; each round per analyte with the result the lab reported, the provider's assigned value and SD, the
/// z-score and its band (|z| ≤ 2 acceptable, ≤ 3 warning, above unacceptable); the corrective action from the CAPA
/// register an unacceptable round needs before it is accepted; and the lab dashboard's summary with overdue rounds.
/// Every change is audited.
/// </summary>
internal static class EqasEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        string[] readers = [DiagnosticsPermissions.LabQcManage, DiagnosticsPermissions.LabResultsVerify];
        var eqas = app.MapGroup("/api/v1/lab/eqas").WithTags("Laboratory EQAS");
        eqas.MapGet("/schemes", ListSchemes).RequireAnyPermission(readers);
        eqas.MapPost("/schemes", (SaveEqasSchemeRequest request, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, CancellationToken ct) =>
                SaveScheme(null, request, db, user, audit, ct))
            .RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<SaveEqasSchemeRequest>();
        eqas.MapPut("/schemes/{id:guid}", (Guid id, SaveEqasSchemeRequest request, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, CancellationToken ct) =>
                SaveScheme(id, request, db, user, audit, ct))
            .RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<SaveEqasSchemeRequest>().RequireVersion();

        eqas.MapGet("/rounds", ListRounds).RequireAnyPermission(readers);
        eqas.MapGet("/summary", Summary).RequireAnyPermission(readers);
        eqas.MapPost("/rounds", Schedule).RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<ScheduleEqasRoundRequest>();
        eqas.MapPut("/rounds/{id:guid}/result", Report).RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<ReportEqasResultRequest>();
        eqas.MapPut("/rounds/{id:guid}/evaluation", Evaluate).RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<EvaluateEqasRoundRequest>();
        eqas.MapPut("/rounds/{id:guid}/capa", LinkCapa).RequirePermission(DiagnosticsPermissions.LabQcManage).WithValidation<LinkEqasCapaRequest>();
        eqas.MapPost("/rounds/{id:guid}/accept", Accept).RequirePermission(DiagnosticsPermissions.LabResultsVerify).WithValidation<AcceptEqasRoundRequest>();
    }

    private static async Task<IResult> ListSchemes(bool? includeInactive, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<EqasScheme>().AsNoTracking()
            .Where(s => includeInactive == true || s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new EqasSchemeDto(s.Id, s.Name, s.Provider, s.ParticipantCode, s.RoundsPerYear, s.IsActive, s.Version))
            .ToListAsync(ct));

    private static async Task<IResult> SaveScheme(
        Guid? id, SaveEqasSchemeRequest request, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, CancellationToken ct)
    {
        EqasScheme scheme;
        if (id is { } existing)
        {
            var found = await db.Set<EqasScheme>().FirstOrDefaultAsync(s => s.Id == existing, ct);
            if (found is null)
            {
                return Error.NotFound(nameof(EqasScheme), existing).ToProblem();
            }

            scheme = found;
            if (request.Version is { } version)
            {
                db.SetExpectedVersion(scheme, version);
            }
        }
        else
        {
            if (user.FacilityId is not { } facilityId)
            {
                return Error.Rule("facility.required", "Select a facility first.").ToProblem();
            }

            scheme = new EqasScheme { FacilityId = facilityId };
            db.Add(scheme);
        }

        var name = request.Name.Trim();
        if (await db.Set<EqasScheme>().AnyAsync(s => s.Name == name && s.Id != scheme.Id, ct))
        {
            return Error.Conflict("eqas.scheme_exists", $"A scheme named {name} already exists.").ToProblem();
        }

        scheme.Name = name;
        scheme.Provider = request.Provider.Trim();
        scheme.ParticipantCode = string.IsNullOrWhiteSpace(request.ParticipantCode) ? null : request.ParticipantCode.Trim();
        scheme.RoundsPerYear = request.RoundsPerYear;
        scheme.IsActive = request.IsActive;
        audit.Add(new AuditEntry(AuditCategories.Clinical, id is null ? "lab.eqas.scheme_added" : "lab.eqas.scheme_changed", $"EQAS scheme {name} ({scheme.Provider}) saved")
        {
            EntityType = nameof(EqasScheme),
            EntityId = scheme.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return id is null ? TypedResults.Created($"/api/v1/lab/eqas/schemes/{scheme.Id}", new { scheme.Id }) : TypedResults.NoContent();
    }

    private static async Task<IResult> ListRounds(
        [AsParameters] PageQuery query, Guid? schemeId, Guid? labTestId, EqasPerformance? performance, bool? overdue, bool? needsCapa,
        DateOnly? from, DateOnly? to, EqasRegister register, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var today = DiagnosticsTime.Today(time);
        var rounds = register.Rounds();
        if (schemeId is not null)
        {
            rounds = rounds.Where(r => r.SchemeId == schemeId);
        }

        if (labTestId is not null)
        {
            rounds = rounds.Where(r => r.LabTestId == labTestId);
        }

        if (performance is not null)
        {
            rounds = rounds.Where(r => r.Performance == performance);
        }

        if (overdue == true)
        {
            rounds = rounds.Where(r => r.ReportedValue == null && r.DueDate < today);
        }

        if (needsCapa == true)
        {
            rounds = rounds.Where(r => r.Performance == EqasPerformance.Unacceptable && r.CapaId == null);
        }

        if (from is not null)
        {
            rounds = rounds.Where(r => r.DueDate >= from);
        }

        if (to is not null)
        {
            rounds = rounds.Where(r => r.DueDate <= to);
        }

        var paged = await rounds.OrderByDescending(r => r.DueDate).ThenBy(r => r.RoundCode).ToPagedResultAsync(page, ct);
        var dtos = await register.ToDtosAsync(paged.Items, ct);
        return TypedResults.Ok(paged.Map(r => dtos[r.Id]));
    }

    private static async Task<IResult> Summary(EqasRegister register, CancellationToken ct) => TypedResults.Ok(await register.SummaryAsync(ct));

    private static async Task<IResult> Schedule(ScheduleEqasRoundRequest request, EqasRegister register, CancellationToken ct) =>
        (await register.ScheduleAsync(request, ct)).ToCreated(id => $"/api/v1/lab/eqas/rounds/{id}");

    private static async Task<IResult> Report(Guid id, ReportEqasResultRequest request, EqasRegister register, CancellationToken ct) =>
        (await register.ReportAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Evaluate(Guid id, EvaluateEqasRoundRequest request, EqasRegister register, CancellationToken ct) =>
        (await register.EvaluateAsync(id, request, ct)).ToOk();

    private static async Task<IResult> LinkCapa(Guid id, LinkEqasCapaRequest request, EqasRegister register, CancellationToken ct) =>
        (await register.LinkCapaAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Accept(Guid id, AcceptEqasRoundRequest request, EqasRegister register, CancellationToken ct) =>
        (await register.AcceptAsync(id, request, ct)).ToOk();
}

/// <summary>The EQAS register's reads and changes (see <see cref="EqasEndpoints"/>).</summary>
internal sealed class EqasRegister(
    HospitalDbContext db,
    DiagnosticsLookups lookups,
    IServiceProvider services,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>How far ahead the dashboard looks for rounds due soon.</summary>
    private const int DueSoonDays = 7;

    public IQueryable<EqasRound> Rounds() => db.Set<EqasRound>().AsNoTracking();

    public async Task<Result<Guid>> ScheduleAsync(ScheduleEqasRoundRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        if (!await db.Set<EqasScheme>().AnyAsync(s => s.Id == request.SchemeId && s.IsActive, ct))
        {
            return Error.Field("schemeId", "Choose an active EQAS scheme.");
        }

        var test = await db.Set<LabTest>().AsNoTracking().Where(t => t.Id == request.LabTestId).Select(t => new { t.Name, t.IsPanel, t.ResultType }).FirstOrDefaultAsync(ct);
        if (test is null || test.IsPanel || test.ResultType != LabResultType.Numeric)
        {
            return Error.Field("labTestId", "Proficiency testing is recorded for a numeric analyte, not a panel.");
        }

        var code = request.RoundCode.Trim();
        if (await db.Set<EqasRound>().AnyAsync(r => r.SchemeId == request.SchemeId && r.LabTestId == request.LabTestId && r.RoundCode == code, ct))
        {
            return Error.Conflict("eqas.round_exists", $"Round {code} of {test.Name} is already recorded for this scheme.");
        }

        var round = new EqasRound
        {
            FacilityId = facilityId,
            SchemeId = request.SchemeId,
            LabTestId = request.LabTestId,
            RoundCode = code,
            DueDate = request.DueDate,
            SampleReceivedOn = request.SampleReceivedOn,
        };
        db.Add(round);
        Audit(round, "lab.eqas.round_scheduled", $"EQAS round {code} of {test.Name} due {request.DueDate:dd-MM-yyyy}", AuditSeverity.Info);
        await db.SaveChangesAsync(ct);
        return round.Id;
    }

    public Task<Result<EqasRoundDto>> ReportAsync(Guid id, ReportEqasResultRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, round => round.Report(request.Value, request.ReportedOn, currentUser.UserId, DiagnosticsTime.Today(time)),
            round => ("lab.eqas.result_reported", $"EQAS round {round.RoundCode}: the lab reported {round.ReportedValue}", AuditSeverity.Info), ct);

    public Task<Result<EqasRoundDto>> EvaluateAsync(Guid id, EvaluateEqasRoundRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version,
            round => round.Evaluate(request.AssignedValue, request.AssignedSd, request.PeerGroup, request.EvaluatedOn, DiagnosticsTime.Today(time)),
            round => ("lab.eqas.evaluated",
                $"EQAS round {round.RoundCode}: reported {round.ReportedValue}, assigned {round.AssignedValue} ± {round.AssignedSd}, z = {round.ZScore:0.00} ({round.Performance})",
                round.Performance == EqasPerformance.Acceptable ? AuditSeverity.Info : AuditSeverity.Warning), ct);

    public async Task<Result<EqasRoundDto>> LinkCapaAsync(Guid id, LinkEqasCapaRequest request, CancellationToken ct)
    {
        if (services.GetService<ICapaRegister>() is not { } capas)
        {
            return Error.Rule("eqas.no_capa_register", "The Quality module's CAPA register is not installed: record the corrective action in the acceptance note.");
        }

        var found = request.CapaId is { } capaId && capaId != Guid.Empty
            ? await capas.FindAsync(capaId, ct)
            : await capas.FindByNumberAsync(request.CapaNumber ?? string.Empty, ct);
        if (found is not { } capa)
        {
            return Error.Field("capaNumber", "No corrective action with that number in this facility's CAPA register.");
        }

        return await ChangeAsync(id, request.Version, round => round.LinkCapa(capa.Id, capa.Number),
            round => ("lab.eqas.capa_linked", $"EQAS round {round.RoundCode}: corrective action {capa.Number} linked", AuditSeverity.Info), ct);
    }

    public Task<Result<EqasRoundDto>> AcceptAsync(Guid id, AcceptEqasRoundRequest request, CancellationToken ct) =>
        ChangeAsync(id, request.Version, round => round.Accept(request.Note, currentUser.UserId, time.GetUtcNow()),
            round => ("lab.eqas.accepted", $"EQAS round {round.RoundCode} accepted ({round.Performance}){(round.AcceptanceNote is { } n ? $": {n}" : string.Empty)}",
                AuditSeverity.Info), ct);

    public async Task<EqasSummary> SummaryAsync(CancellationToken ct)
    {
        var today = DiagnosticsTime.Today(time);
        var soon = today.AddDays(DueSoonDays);
        var yearAgo = today.AddYears(-1);
        var rounds = Rounds();
        var overdue = await rounds.CountAsync(r => r.ReportedValue == null && r.DueDate < today, ct);
        var dueSoon = await rounds.CountAsync(r => r.ReportedValue == null && r.DueDate >= today && r.DueDate <= soon, ct);
        var needCapa = await rounds.CountAsync(r => r.Performance == EqasPerformance.Unacceptable && r.CapaId == null, ct);
        var bands = await rounds.Where(r => r.Performance != null && r.EvaluatedOn >= yearAgo)
            .GroupBy(r => r.Performance)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key!.Value, g => g.Count, ct);
        var attention = await rounds
            .Where(r => (r.ReportedValue == null && r.DueDate <= soon) || (r.Performance == EqasPerformance.Unacceptable && r.CapaId == null))
            .OrderBy(r => r.DueDate)
            .Take(10)
            .ToListAsync(ct);
        var dtos = await ToDtosAsync(attention, ct);
        return new EqasSummary(overdue, dueSoon, needCapa, bands.GetValueOrDefault(EqasPerformance.Acceptable), bands.GetValueOrDefault(EqasPerformance.Warning),
            bands.GetValueOrDefault(EqasPerformance.Unacceptable), [.. attention.Select(r => dtos[r.Id])]);
    }

    public async Task<IReadOnlyDictionary<Guid, EqasRoundDto>> ToDtosAsync(IReadOnlyCollection<EqasRound> rounds, CancellationToken ct)
    {
        var today = DiagnosticsTime.Today(time);
        var schemeIds = rounds.Select(r => r.SchemeId).Distinct().ToList();
        var testIds = rounds.Select(r => r.LabTestId).Distinct().ToList();
        var schemes = await db.Set<EqasScheme>().AsNoTracking().Where(s => schemeIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var tests = await db.Set<LabTest>().AsNoTracking().Where(t => testIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Code, t.Name, t.Unit }).ToDictionaryAsync(t => t.Id, ct);
        var users = await lookups.UserNamesAsync(rounds.Select(r => r.AcceptedBy), ct);
        var capaIds = rounds.Where(r => r.CapaId is not null).Select(r => r.CapaId!.Value).ToList();
        var capas = services.GetService<ICapaRegister>() is { } register && capaIds.Count > 0
            ? await register.FindManyAsync(capaIds, ct)
            : new Dictionary<Guid, CapaLink>();
        return rounds.ToDictionary(r => r.Id, r =>
        {
            var scheme = schemes[r.SchemeId];
            var test = tests[r.LabTestId];
            return new EqasRoundDto(
                r.Id, r.SchemeId, scheme.Name, scheme.Provider, r.LabTestId, test.Code, test.Name, test.Unit, r.RoundCode, r.DueDate, r.SampleReceivedOn,
                r.ReportedValue, r.ReportedOn, r.AssignedValue, r.AssignedSd, r.PeerGroup, r.EvaluatedOn, r.ZScore, r.Performance, r.IsOverdue(today),
                r.NeedsCapa, r.CapaId, r.CapaNumber, r.CapaId is { } capaId ? capas.GetValueOrDefault(capaId)?.Status : null, r.AcceptedAt,
                r.AcceptedBy is { } by ? users.GetValueOrDefault(by) : null, r.AcceptanceNote, r.Version);
        });
    }

    private async Task<Result<EqasRoundDto>> ChangeAsync(
        Guid id, uint version, Func<EqasRound, Result> change, Func<EqasRound, (string Action, string Message, AuditSeverity Severity)> describe, CancellationToken ct)
    {
        var round = await db.Set<EqasRound>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (round is null)
        {
            return Error.NotFound(nameof(EqasRound), id);
        }

        db.SetExpectedVersion(round, version);
        var changed = change(round);
        if (changed.IsFailure)
        {
            return changed.Error!;
        }

        var (action, message, severity) = describe(round);
        Audit(round, action, message, severity);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([round], ct))[round.Id];
    }

    private void Audit(EqasRound round, string action, string message, AuditSeverity severity) =>
        audit.Add(new AuditEntry(AuditCategories.Clinical, action, message)
        {
            EntityType = nameof(EqasRound),
            EntityId = round.Id.ToString(),
            Severity = severity,
        });
}
