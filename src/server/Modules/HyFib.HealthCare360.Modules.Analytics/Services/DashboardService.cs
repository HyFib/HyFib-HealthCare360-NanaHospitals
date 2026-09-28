using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Dashboards;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Services;

/// <summary>
/// Saved dashboards (BI-01): list, read, create, update, copy and delete, with layout validation. Every widget must name
/// a catalogue report the author may open, with valid inputs; reports that list patients cannot be placed on dashboards
/// (a dashboard is often left open on a wall screen).
/// </summary>
internal sealed class DashboardService(HospitalDbContext db, ReportCatalog catalog, TimeProvider time)
{
    public async Task<IReadOnlyList<DashboardListItem>> ListAsync(ReportActor actor, CancellationToken ct)
    {
        var userId = actor.UserId;
        var rows = await db.Set<Dashboard>().AsNoTracking()
            .Where(d => d.IsShared || d.OwnerUserId == null || d.OwnerUserId == userId)
            .OrderBy(d => d.Name)
            .Take(500)
            .ToListAsync(ct);
        var canManage = actor.Has(AnalyticsPermissions.DashboardsManage);
        return rows
            .OrderByDescending(d => d.OwnerUserId == userId).ThenBy(d => d.IsTemplate).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d => new DashboardListItem(
                d.Id, d.Name, d.Description, d.IsShared, d.IsTemplate, d.OwnerUserId == userId, d.IsEditableBy(userId, canManage), d.OwnerName,
                DashboardLayout.Deserialize(d.LayoutJson).Count, d.UpdatedAt ?? d.CreatedAt, d.Version))
            .ToList();
    }

    public async Task<Result<DashboardDetail>> GetAsync(ReportActor actor, Guid id, CancellationToken ct)
    {
        var dashboard = await db.Set<Dashboard>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dashboard is null || !dashboard.IsVisibleTo(actor.UserId))
        {
            return Error.NotFound("Dashboard", id);
        }

        return ToDetail(dashboard, actor);
    }

    public async Task<Result<DashboardDetail>> CreateAsync(ReportActor actor, SaveDashboardRequest request, CancellationToken ct)
    {
        var widgets = ValidateWidgets(actor, request.Widgets);
        if (widgets.IsFailure)
        {
            return widgets.Error!;
        }

        var dashboard = new Dashboard
        {
            Name = request.Name.Trim(),
            Description = Clean(request.Description),
            IsShared = request.IsShared,
            OwnerUserId = actor.UserId,
            OwnerName = actor.DisplayName,
            LayoutJson = DashboardLayout.Serialize(widgets.Value),
        };
        db.Add(dashboard);
        await db.SaveChangesAsync(ct);
        return ToDetail(dashboard, actor);
    }

    public async Task<Result<DashboardDetail>> UpdateAsync(ReportActor actor, Guid id, SaveDashboardRequest request, CancellationToken ct)
    {
        var dashboard = await db.Set<Dashboard>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dashboard is null || !dashboard.IsVisibleTo(actor.UserId))
        {
            return Error.NotFound("Dashboard", id);
        }

        if (!dashboard.IsEditableBy(actor.UserId, actor.Has(AnalyticsPermissions.DashboardsManage)))
        {
            return Error.Forbidden("analytics.dashboard_readonly", "Only the person who created this dashboard can change it. Make a copy to adapt it.");
        }

        var widgets = ValidateWidgets(actor, request.Widgets);
        if (widgets.IsFailure)
        {
            return widgets.Error!;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(dashboard, version);
        }

        dashboard.Name = request.Name.Trim();
        dashboard.Description = Clean(request.Description);
        dashboard.IsShared = dashboard.IsTemplate || request.IsShared;
        dashboard.LayoutJson = DashboardLayout.Serialize(widgets.Value);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.ConcurrencyConflict;
        }

        return ToDetail(dashboard, actor);
    }

    /// <summary>A personal copy of any visible dashboard (the way to adapt someone else's or a template).</summary>
    public async Task<Result<DashboardDetail>> CopyAsync(ReportActor actor, Guid id, CancellationToken ct)
    {
        var source = await db.Set<Dashboard>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (source is null || !source.IsVisibleTo(actor.UserId))
        {
            return Error.NotFound("Dashboard", id);
        }

        var copy = new Dashboard
        {
            Name = Truncate($"{source.Name} (copy)", 100),
            Description = source.Description,
            IsShared = false,
            OwnerUserId = actor.UserId,
            OwnerName = actor.DisplayName,
            LayoutJson = source.LayoutJson,
        };
        db.Add(copy);
        await db.SaveChangesAsync(ct);
        return ToDetail(copy, actor);
    }

    public async Task<Result> DeleteAsync(ReportActor actor, Guid id, uint? version, CancellationToken ct)
    {
        var dashboard = await db.Set<Dashboard>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dashboard is null || !dashboard.IsVisibleTo(actor.UserId))
        {
            return Error.NotFound("Dashboard", id);
        }

        if (!dashboard.IsEditableBy(actor.UserId, actor.Has(AnalyticsPermissions.DashboardsManage)))
        {
            return Error.Forbidden("analytics.dashboard_readonly", "Only the person who created this dashboard can delete it.");
        }

        if (version is { } expected)
        {
            db.SetExpectedVersion(dashboard, expected);
        }

        db.Remove(dashboard);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.ConcurrencyConflict;
        }

        return Result.Success();
    }

    /// <summary>Checks every widget against the catalogue and the author's access; stores inputs in canonical form.</summary>
    internal Result<IReadOnlyList<DashboardWidget>> ValidateWidgets(ReportActor actor, IReadOnlyList<SaveDashboardWidget>? widgets)
    {
        var result = new List<DashboardWidget>();
        var today = AnalyticsClock.Today(time);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (widget, index) in (widgets ?? []).Select((w, i) => (w, i)))
        {
            var field = $"widgets[{index}]";
            var report = catalog.Find(widget.ReportId)?.Descriptor;
            if (report is null)
            {
                return Error.Field($"{field}.reportId", "Choose a report from the catalogue.");
            }

            if (!ReportCatalog.CanRead(report, actor))
            {
                return Error.Field($"{field}.reportId", $"You do not have access to the report '{report.Title}'.");
            }

            if (report.ContainsPatientIdentifiers)
            {
                return Error.Field($"{field}.reportId", $"'{report.Title}' lists patients and cannot be placed on a dashboard.");
            }

            if (report.Chart is null && widget.View is not (ReportChartType.Table or ReportChartType.Summary))
            {
                return Error.Field($"{field}.view", $"'{report.Title}' has no chart; show it as a table or as headline figures.");
            }

            var arguments = ReportParameterBinder.Bind(report, widget.Parameters, today);
            if (arguments.IsFailure)
            {
                var message = arguments.Error!.Fields?.Values.SelectMany(v => v).FirstOrDefault() ?? arguments.Error.Message;
                return Error.Field($"{field}.parameters", $"{report.Title}: {message}");
            }

            var id = string.IsNullOrWhiteSpace(widget.Id) ? $"w{index + 1}" : widget.Id.Trim();
            if (!ids.Add(id))
            {
                id = $"w{index + 1}-{Guid.NewGuid().ToString("N")[..6]}";
                ids.Add(id);
            }

            result.Add(new DashboardWidget(
                id, report.Id, Clean(widget.Title) ?? report.Title, widget.View, DashboardLayout.Widths.Contains(widget.Width) ? widget.Width : 2,
                arguments.Value.ToCanonical()));
        }

        return result;
    }

    private DashboardDetail ToDetail(Dashboard dashboard, ReportActor actor)
    {
        var widgets = DashboardLayout.Deserialize(dashboard.LayoutJson)
            .Select(w =>
            {
                var report = catalog.Find(w.ReportId)?.Descriptor;
                var canView = report is not null && ReportCatalog.CanRead(report, actor) && !report.ContainsPatientIdentifiers;
                return new DashboardWidgetDto(w.Id, w.ReportId, w.Title ?? report?.Title ?? w.ReportId, w.View, w.Width, w.Parameters, canView, report?.Title);
            })
            .ToList();
        return new DashboardDetail(
            dashboard.Id, dashboard.Name, dashboard.Description, dashboard.IsShared, dashboard.IsTemplate, dashboard.OwnerUserId == actor.UserId,
            dashboard.IsEditableBy(actor.UserId, actor.Has(AnalyticsPermissions.DashboardsManage)), dashboard.OwnerName, widgets,
            dashboard.UpdatedAt ?? dashboard.CreatedAt, dashboard.Version);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
