using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Reports;

public sealed record ReportCategoryDto(ReportCategory Id, string Label, int Count);

/// <summary>A catalogue entry: what the report shows, how it is calculated and what it asks for.</summary>
public sealed record ReportCatalogueItem(
    string Id,
    string Title,
    ReportCategory Category,
    string CategoryLabel,
    string Description,
    string Definition,
    IReadOnlyList<ReportParameter> Parameters,
    IReadOnlyList<ReportColumn> Columns,
    ReportChartHint? Chart,
    bool ContainsPatientIdentifiers,
    bool CanSchedule,
    int MaxDays);

/// <summary>GET /api/v1/analytics/reports — the reports the caller may open, with the choices the report form needs.</summary>
public sealed record ReportCatalogueDto(
    IReadOnlyList<ReportCategoryDto> Categories,
    IReadOnlyList<ReportCatalogueItem> Reports,
    IReadOnlyList<PeriodPreset> Periods,
    bool CanManageDashboards,
    bool CanManageSchedules,
    bool CanSeePatientLevel,
    bool CanConsolidate,
    string DataSource);

/// <summary>Report inputs (parameter key → value), paging, sorting and an optional chart type for the returned chart.</summary>
public sealed record RunReportRequest(
    Dictionary<string, string?>? Parameters,
    int? Page = null,
    int? PageSize = null,
    string? Sort = null,
    bool? Desc = null,
    ReportChartType? Chart = null);

public sealed record ExportReportRequest(Dictionary<string, string?>? Parameters, string? Sort = null, bool? Desc = null);

/// <summary>An input as shown to people ("Department: Cardiology").</summary>
public sealed record ReportFilterDto(string Label, string Value);

/// <summary>One page of a report with totals, headline figures, chart data and drill-down.</summary>
public sealed record ReportResultDto(
    string ReportId,
    string Title,
    ReportCategory Category,
    DateOnly? From,
    DateOnly? To,
    string FacilityName,
    IReadOnlyList<ReportFilterDto> Filters,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<ReportColumn> Columns,
    IReadOnlyList<object?[]> Rows,
    IReadOnlyList<string?>? RowKeys,
    object?[]? Totals,
    IReadOnlyList<ReportFigure> Summary,
    ReportChartData? Chart,
    ReportDrillDown? DrillDown,
    IReadOnlyList<string> Notes,
    int Page,
    int PageSize,
    int TotalRows,
    bool Truncated,
    string? Sort,
    bool Desc,
    DateTimeOffset GeneratedAt,
    string DataSource,
    bool ContainsPatientIdentifiers);

/// <summary>GET /api/v1/analytics/status — where reports read from and how fresh the data is (BI-05).</summary>
public sealed record AnalyticsStatusDto(
    string DataSource,
    bool UsesReplica,
    double? ReplicaLagSeconds,
    int ActiveSchedules,
    DateTimeOffset? LastDeliveryAt,
    int FailedDeliveriesLast7Days,
    DateTimeOffset CheckedAt);

public sealed class RunReportRequestValidator : AbstractValidator<RunReportRequest>
{
    public RunReportRequestValidator()
    {
        RuleFor(x => x.Page).InclusiveBetween(1, 10_000).When(x => x.Page is not null);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ReportPresenter.MaxPageSize).When(x => x.PageSize is not null);
        RuleFor(x => x.Sort).MaximumLength(64);
        RuleFor(x => x.Parameters).Must(p => p is null || p.Count <= 30).WithMessage("Too many report inputs.");
        RuleFor(x => x.Chart).IsInEnum().When(x => x.Chart is not null);
    }
}

public sealed class ExportReportRequestValidator : AbstractValidator<ExportReportRequest>
{
    public ExportReportRequestValidator()
    {
        RuleFor(x => x.Sort).MaximumLength(64);
        RuleFor(x => x.Parameters).Must(p => p is null || p.Count <= 30).WithMessage("Too many report inputs.");
    }
}
