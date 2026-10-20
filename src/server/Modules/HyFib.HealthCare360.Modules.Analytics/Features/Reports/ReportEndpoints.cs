using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Export;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Documents;
using Npgsql;
using Theatre = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Reports;

/// <summary>/api/v1/analytics — report catalogue, run, export, look-ups, KPIs and data status.</summary>
internal static class ReportEndpoints
{
    /// <summary>
    /// Response header of an export that leaves rows out (the PDF of a very long report): what the file lists, and that
    /// the Excel export has every row.
    /// </summary>
    public const string NoticeHeader = "X-Report-Notice";

    /// <summary>Query keys of the GET export that are not report inputs.</summary>
    private static readonly HashSet<string> ExportControlKeys = new(StringComparer.OrdinalIgnoreCase) { "format", "sort", "desc" };

    /// <summary>Query keys of the GET run that are not report inputs.</summary>
    private static readonly HashSet<string> RunControlKeys = new(StringComparer.OrdinalIgnoreCase) { "page", "pageSize", "sort", "desc", "chart" };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/analytics").WithTags("Analytics");

        group.MapGet("/reports", Catalogue).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapGet("/reports/{id}", Describe).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapPost("/reports/{id}/run", Run).RequireAnyPermission(AnalyticsPermissions.AnyRead).WithValidation<RunReportRequest>();

        // The same read with inputs in the query string. The staff app uses it: GET requests are not stored by the
        // idempotency middleware, so dashboards refreshing many widgets write nothing to the primary database.
        group.MapGet("/reports/{id}/run", RunGet).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapPost("/reports/{id}/export", ExportPost).RequireAnyPermission(AnalyticsPermissions.AnyRead).WithValidation<ExportReportRequest>();

        // Same export for browser downloads (the staff app's api.blob issues GET requests); inputs come from the query.
        group.MapGet("/reports/{id}/export", ExportGet).RequireAnyPermission(AnalyticsPermissions.AnyRead);

        group.MapGet("/kpis", Kpis).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapGet("/kpi-definitions", KpiDefinitionsCatalogue).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapGet("/lookups/{kind}", Lookups).RequireAnyPermission(AnalyticsPermissions.AnyRead);
        group.MapGet("/status", Status).RequireAnyPermission(AnalyticsPermissions.AnyRead);
    }

    private static async Task<IResult> Catalogue(ReportActors actors, ReportCatalog catalog, FacilityAccess facilities, ReportingDatabase reporting, CancellationToken ct)
    {
        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        var visible = catalog.VisibleTo(actor).Select(r => r.Descriptor).ToList();
        var categories = Enum.GetValues<ReportCategory>()
            .Select(c => new ReportCategoryDto(c, ReportCatalog.CategoryLabel(c), visible.Count(r => r.Category == c)))
            .Where(c => c.Count > 0)
            .ToList();
        return TypedResults.Ok(new ReportCatalogueDto(
            categories,
            visible.Select(ToItem).ToList(),
            ReportPeriods.All,
            actor.Has(AnalyticsPermissions.DashboardsManage),
            actor.Has(AnalyticsPermissions.SchedulesManage),
            actor.Has(AnalyticsPermissions.ClinicalRead),
            await facilities.CanConsolidateAsync(actor, ct),
            reporting.SourceLabel));
    }

    private static async Task<IResult> Describe(string id, ReportActors actors, ReportCatalog catalog, CancellationToken ct)
    {
        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        var report = catalog.Find(id);
        if (report is null)
        {
            return Error.NotFound("Report", id).ToProblem();
        }

        return ReportCatalog.CanRead(report.Descriptor, actor)
            ? TypedResults.Ok(ToItem(report.Descriptor))
            : Error.Forbidden("analytics.report_forbidden", "You do not have access to this report.").ToProblem();
    }

    private static Task<IResult> Run(
        string id, RunReportRequest request, ReportActors actors, ReportRunner runner, ReportPresenter presenter, ReportAuditor auditor, CancellationToken ct) =>
        RunAsync(id, request, actors, runner, presenter, auditor, ct);

    private static Task<IResult> RunGet(
        string id, int? page, int? pageSize, string? sort, bool? desc, ReportChartType? chart, HttpContext http, ReportActors actors, ReportRunner runner,
        ReportPresenter presenter, ReportAuditor auditor, CancellationToken ct)
    {
        var parameters = http.Request.Query
            .Where(q => !RunControlKeys.Contains(q.Key))
            .Take(30)
            .ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.Ordinal);
        var request = new RunReportRequest(
            parameters,
            page is null ? null : Math.Clamp(page.Value, 1, 10_000),
            pageSize is null ? null : Math.Clamp(pageSize.Value, 1, ReportPresenter.MaxPageSize),
            sort is { Length: > 64 } ? null : sort,
            desc,
            chart is { } type && Enum.IsDefined(type) ? type : null);
        return RunAsync(id, request, actors, runner, presenter, auditor, ct);
    }

    private static async Task<IResult> RunAsync(
        string id, RunReportRequest request, ReportActors actors, ReportRunner runner, ReportPresenter presenter, ReportAuditor auditor, CancellationToken ct)
    {
        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        var execution = await runner.RunAsync(actor, id, request.Parameters, ct);
        if (execution.IsFailure)
        {
            return execution.Error!.ToProblem();
        }

        var filters = await presenter.DescribeAsync(execution.Value, ct);
        var (result, patients) = presenter.Present(execution.Value, filters, request.Page, request.PageSize, request.Sort, request.Desc, request.Chart);
        await auditor.PageViewedAsync(execution.Value, patients, ct);
        return TypedResults.Ok(result);
    }

    private static Task<IResult> ExportPost(
        string id, string? format, ExportReportRequest request, HttpContext http, ReportActors actors, ReportRunner runner, ReportPresenter presenter,
        ReportFileWriter writer, ReportAuditor auditor, CancellationToken ct) =>
        ExportAsync(id, format, request.Parameters, request.Sort, request.Desc, http, actors, runner, presenter, writer, auditor, ct);

    private static Task<IResult> ExportGet(
        string id, string? format, string? sort, bool? desc, HttpContext http, ReportActors actors, ReportRunner runner, ReportPresenter presenter,
        ReportFileWriter writer, ReportAuditor auditor, CancellationToken ct)
    {
        var parameters = http.Request.Query
            .Where(q => !ExportControlKeys.Contains(q.Key))
            .Take(30)
            .ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.Ordinal);
        return ExportAsync(id, format, parameters, sort, desc, http, actors, runner, presenter, writer, auditor, ct);
    }

    private static async Task<IResult> ExportAsync(
        string id, string? format, IReadOnlyDictionary<string, string?>? parameters, string? sort, bool? desc, HttpContext http, ReportActors actors,
        ReportRunner runner, ReportPresenter presenter, ReportFileWriter writer, ReportAuditor auditor, CancellationToken ct)
    {
        if (!TryFormat(format, out var exportFormat))
        {
            return Error.Field("format", "Choose Excel (xlsx), CSV or PDF.").ToProblem();
        }

        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        var execution = await runner.RunAsync(actor, id, parameters, ct);
        if (execution.IsFailure)
        {
            return execution.Error!.ToProblem();
        }

        var filters = await presenter.DescribeAsync(execution.Value, ct);
        var document = presenter.Document(execution.Value, filters, Person(actor), sort, desc);
        ReportFile file;
        try
        {
            file = await writer.WriteAsync(document, exportFormat, ct);
        }
        catch (PdfRenderException ex)
        {
            return PdfUnavailable(ex);
        }

        // Audited for the rows in the file: a PDF of a very long patient list names only the patients it lists.
        await auditor.ExportedAsync(execution.Value, exportFormat, file.Rows, actor, "download", ReportPresenter.PatientsIn(execution.Value, document, file.Rows), ct);

        // Exports can contain confidential or patient data: never cache them on the workstation or a proxy.
        http.Response.Headers.CacheControl = "no-store";
        if (file.Notice is { } notice)
        {
            http.Response.Headers[NoticeHeader] = notice;
        }

        return TypedResults.File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// The answer when the server cannot make the PDF, as every document's PDF answers (<c>DocumentResults.ToPdfAsync</c>):
    /// 503 with the reason and the code <c>pdf.unavailable</c>, so the screen can say so and offer Excel instead.
    /// </summary>
    internal static IResult PdfUnavailable(PdfRenderException error) => DocumentResults.PdfUnavailable($"{error.Message} Export to Excel or CSV instead.");

    private static async Task<IResult> Kpis(DateOnly? from, DateOnly? to, string? facilityId, ReportActors actors, KpiService kpis, CancellationToken ct)
    {
        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        return (await kpis.GetAsync(actor, from, to, facilityId, ct)).ToOk();
    }

    private static async Task<IResult> KpiDefinitionsCatalogue(ReportActors actors, ReportCatalog catalog, CancellationToken ct) =>
        await actors.CurrentAsync(ct) is { } actor ? TypedResults.Ok(DescribeDefinitions(catalog, actor)) : SignInRequired();

    /// <summary>
    /// The KPIs and reports the actor can see, as the dashboard and the catalogue show them: a KPI the dashboard would
    /// leave out is left out here too, and a report the actor may not open is neither listed nor named.
    /// </summary>
    internal static KpiDefinitionsDto DescribeDefinitions(ReportCatalog catalog, ReportActor actor)
    {
        var reports = catalog.VisibleTo(actor).Select(r => r.Descriptor).ToList();
        var titles = reports.ToDictionary(r => r.Id, r => r.Title, StringComparer.OrdinalIgnoreCase);
        var kpis = KpiDefinitions.All.Where(k => KpiService.CanSee(k, actor)).ToList();
        return new KpiDefinitionsDto(
            kpis.Select(k => new KpiCatalogueItem(
                    k.Key, k.Label, k.Unit, k.HigherIsBetter, k.Formula, k.Source, k.Target, k.Audience, k.ReportId, titles.GetValueOrDefault(k.ReportId)))
                .ToList(),
            reports
                .Select(r => new ReportDefinitionItem(
                    r.Id, r.Title, r.Category, ReportCatalog.CategoryLabel(r.Category), r.Description, r.Definition, r.ContainsPatientIdentifiers, r.CanSchedule,
                    kpis.Where(k => string.Equals(k.ReportId, r.Id, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList()))
                .ToList());
    }

    /// <summary>Choices for report inputs, readable by every analytics user (other modules' master screens need their own permissions).</summary>
    private static async Task<IResult> Lookups(
        string kind, string? search, string? facilityId, ReportActors actors, FacilityAccess facilities, ReportingDbContextFactory contexts, CancellationToken ct)
    {
        if (await actors.CurrentAsync(ct) is not { } actor)
        {
            return SignInRequired();
        }

        if (string.Equals(kind, "facilities", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Ok(await facilities.AllowedAsync(actor, ct));
        }

        var scope = await facilities.ResolveAsync(actor, facilityId, ct);
        if (scope.IsFailure)
        {
            return scope.Error!.ToProblem();
        }

        var like = string.IsNullOrWhiteSpace(search) ? null : search.Trim()[..Math.Min(search.Trim().Length, 60)].ToLikePattern();
        var facility = scope.Value.FacilityId;
        await using var db = contexts.Create(facility);
        IQueryable<LookupItem>? items = kind.ToLowerInvariant() switch
        {
            "departments" => db.Set<Department>().Where(d => d.IsActive && (like == null || EF.Functions.ILike(d.Name, like, QueryExtensions.LikeEscape)))
                .OrderBy(d => d.DisplayOrder).ThenBy(d => d.Name).Select(d => new LookupItem(d.Id, d.Code, d.Name)),
            "practitioners" => db.Set<Practitioner>()
                .Where(p => p.IsActive && (facility == null || p.FacilityIds.Count == 0 || p.FacilityIds.Contains(facility.Value))
                            && (like == null || EF.Functions.ILike(p.DisplayName, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(p.Code, like, QueryExtensions.LikeEscape)))
                .OrderBy(p => p.DisplayName).Select(p => new LookupItem(p.Id, p.Code, p.DisplayName)),
            "payers" => db.Set<Payer>().Where(p => p.IsActive && (like == null || EF.Functions.ILike(p.Name, like, QueryExtensions.LikeEscape)))
                .OrderBy(p => p.Name).Select(p => new LookupItem(p.Id, p.Code, p.Name)),
            "wards" => db.Set<Ward>().Where(w => w.IsActive && (like == null || EF.Functions.ILike(w.Name, like, QueryExtensions.LikeEscape)))
                .OrderBy(w => w.DisplayOrder).ThenBy(w => w.Name).Select(w => new LookupItem(w.Id, w.Code, w.Name)),
            "stores" => db.Set<Store>().Where(s => s.IsActive && (like == null || EF.Functions.ILike(s.Name, like, QueryExtensions.LikeEscape)))
                .OrderBy(s => s.Name).Select(s => new LookupItem(s.Id, s.Code, s.Name)),
            "theatres" => db.Set<Theatre>().Where(t => t.IsActive && (like == null || EF.Functions.ILike(t.Name, like, QueryExtensions.LikeEscape)))
                .OrderBy(t => t.DisplayOrder).ThenBy(t => t.Code).Select(t => new LookupItem(t.Id, t.Code, t.Code + " · " + t.Name)),
            _ => null,
        };

        return items is null
            ? Error.NotFound("Look-up", kind).ToProblem()
            : TypedResults.Ok(await items.Take(200).ToListAsync(ct));
    }

    private static async Task<IResult> Status(
        ReportingDatabase reporting, ReportingDbContextFactory contexts, HospitalDbContext db, ICurrentUser user, TimeProvider time, CancellationToken ct)
    {
        double? lag = null;
        if (reporting.UsesReplica)
        {
            try
            {
                await using var replica = contexts.Create(user.FacilityId);
                lag = await replica.Database
                    .SqlQueryRaw<double?>("SELECT EXTRACT(EPOCH FROM (now() - pg_last_xact_replay_timestamp()))::float8 AS \"Value\"")
                    .FirstOrDefaultAsync(ct);
            }
            catch (NpgsqlException)
            {
                lag = null;
            }
        }

        var now = time.GetUtcNow();
        var since = now.AddDays(-7);

        // E-mail figures leave out the extracts' runs (deleted extracts' too), which the extract figures cover.
        var extractIds = db.Set<ReportSchedule>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter])
            .Where(s => s.Destination == ScheduleDestination.ExtractFolder)
            .Select(s => s.Id);
        var emailDeliveries = db.Set<ReportDelivery>().Where(d => !extractIds.Contains(d.ScheduleId));
        var extracts = await db.Set<ReportSchedule>().AsNoTracking().Where(s => s.Destination == ScheduleDestination.ExtractFolder).ToListAsync(ct);
        return TypedResults.Ok(new AnalyticsStatusDto(
            reporting.SourceLabel,
            reporting.UsesReplica,
            lag is { } seconds ? Math.Round(seconds, 1) : null,
            await db.Set<ReportSchedule>().CountAsync(s => s.IsActive && s.Destination == ScheduleDestination.Email, ct),
            await emailDeliveries.MaxAsync(d => (DateTimeOffset?)d.CreatedAt, ct),
            await emailDeliveries.CountAsync(d => d.CreatedAt >= since && d.Status == DeliveryStatus.Failed, ct),
            now,
            extracts.Count(e => e.IsActive),
            extracts.Count(e => e.State(now) == ExtractState.Late),
            extracts.Count(e => e.State(now) == ExtractState.Failing),
            extracts.Max(e => e.LastSucceededAt)));
    }

    internal static ReportCatalogueItem ToItem(ReportDescriptor report) => new(
        report.Id, report.Title, report.Category, ReportCatalog.CategoryLabel(report.Category), report.Description, report.Definition, report.Parameters,
        report.Columns, report.Chart, report.ContainsPatientIdentifiers, report.CanSchedule, report.MaxDays);

    internal static string Person(ReportActor actor) =>
        string.IsNullOrWhiteSpace(actor.UserName) || actor.UserName == actor.DisplayName ? actor.DisplayName : $"{actor.DisplayName} ({actor.UserName})";

    internal static bool TryFormat(string? value, out ExportFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "xlsx" or "excel":
                format = ExportFormat.Xlsx;
                return true;
            case "csv":
                format = ExportFormat.Csv;
                return true;
            case "pdf":
                format = ExportFormat.Pdf;
                return true;
            default:
                format = default;
                return false;
        }
    }

    private static IResult SignInRequired() => Error.Forbidden("auth.required", "Sign in as a staff user to use analytics.").ToProblem();
}
