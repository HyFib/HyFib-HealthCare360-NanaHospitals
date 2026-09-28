using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Dashboards;

public sealed record DashboardListItem(
    Guid Id,
    string Name,
    string? Description,
    bool IsShared,
    bool IsTemplate,
    bool IsMine,
    bool CanEdit,
    string? OwnerName,
    int WidgetCount,
    DateTimeOffset UpdatedAt,
    uint Version);

/// <summary>A widget as the viewer sees it: <see cref="CanView"/> is false when the viewer may not open its report.</summary>
public sealed record DashboardWidgetDto(
    string Id,
    string ReportId,
    string Title,
    ReportChartType View,
    int Width,
    IReadOnlyDictionary<string, string> Parameters,
    bool CanView,
    string? ReportTitle);

public sealed record DashboardDetail(
    Guid Id,
    string Name,
    string? Description,
    bool IsShared,
    bool IsTemplate,
    bool IsMine,
    bool CanEdit,
    string? OwnerName,
    IReadOnlyList<DashboardWidgetDto> Widgets,
    DateTimeOffset UpdatedAt,
    uint Version);

public sealed record SaveDashboardWidget(string? Id, string ReportId, string? Title, ReportChartType View, int Width, Dictionary<string, string?>? Parameters);

public sealed record SaveDashboardRequest(string Name, string? Description, bool IsShared, IReadOnlyList<SaveDashboardWidget>? Widgets, uint? Version = null);

public sealed class SaveDashboardRequestValidator : AbstractValidator<SaveDashboardRequest>
{
    public SaveDashboardRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Give the dashboard a name.").SafeText(100);
        RuleFor(x => x.Description).SafeText(300);
        RuleFor(x => x.Widgets).Must(w => w is null || w.Count <= Dashboard.MaxWidgets)
            .WithMessage($"A dashboard can have at most {Dashboard.MaxWidgets} widgets.");
        RuleForEach(x => x.Widgets).ChildRules(widget =>
        {
            widget.RuleFor(w => w.ReportId).NotEmpty().MaximumLength(64);
            widget.RuleFor(w => w.Id).MaximumLength(40).Matches("^[A-Za-z0-9_-]*$").WithMessage("Use letters, digits, dashes or underscores.");
            widget.RuleFor(w => w.Title).SafeText(80);
            widget.RuleFor(w => w.View).IsInEnum();
            widget.RuleFor(w => w.Width).Must(w => DashboardLayout.Widths.Contains(w)).WithMessage("Choose a width of 1, 2 or 4 columns.");
            widget.RuleFor(w => w.Parameters).Must(p => p is null || p.Count <= 30).WithMessage("Too many report inputs.");
        });
    }
}
