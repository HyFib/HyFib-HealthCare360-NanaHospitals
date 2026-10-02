using System.Globalization;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Reports.Export;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Options;
using Theatre = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>
/// Turns a <see cref="ReportExecution"/> into what people see: the filters in words, one sorted page with totals and
/// chart data for the screen, or the whole sorted report for an export.
/// </summary>
internal sealed class ReportPresenter(HospitalDbContext db, IOptions<PlatformOptions> platform)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 500;

    /// <summary>The report's inputs in plain words (for the screen header and the export header).</summary>
    public async Task<IReadOnlyList<ReportFilterDto>> DescribeAsync(ReportExecution execution, CancellationToken ct)
    {
        var args = execution.Arguments;
        var filters = new List<ReportFilterDto>();
        if (!execution.Report.HasPeriod)
        {
            filters.Add(new("As of", AnalyticsClock.FormatDateTime(execution.GeneratedAt)));
        }

        foreach (var parameter in execution.Report.Parameters)
        {
            switch (parameter.Kind)
            {
                case ReportParameterKind.Period:
                    var preset = args.Period is { } p && p != ReportPeriods.Custom ? $" ({ReportPeriods.LabelOf(p)})" : string.Empty;
                    filters.Add(new("Period", ReportPeriods.Describe(args.From, args.To) + preset));
                    break;

                case ReportParameterKind.Facility:
                    filters.Add(new("Facility", execution.Facility.Name));
                    break;

                case ReportParameterKind.Choice:
                    if (args.Text(parameter.Key) is { } choice)
                    {
                        filters.Add(new(parameter.Label, parameter.Choices?.FirstOrDefault(c => c.Value == choice)?.Label ?? choice));
                    }

                    break;

                case ReportParameterKind.Number:
                    if (args.NumberOf(parameter.Key) is { } number)
                    {
                        filters.Add(new(parameter.Label, number.ToString("N0", CultureInfo.InvariantCulture)));
                    }

                    break;

                default:
                    if (args.GuidOf(parameter.Key) is { } id)
                    {
                        filters.Add(new(parameter.Label, await NameAsync(parameter.Kind, id, ct) ?? "Unknown"));
                    }

                    break;
            }
        }

        return filters;
    }

    /// <summary>One sorted page for the screen, and the patients shown on it (for the audit trail).</summary>
    public (ReportResultDto Result, IReadOnlyList<Guid> PatientsOnPage) Present(
        ReportExecution execution, IReadOnlyList<ReportFilterDto> filters, int? page, int? pageSize, string? sort, bool? desc, ReportChartType? chart)
    {
        var data = execution.Data;
        var (order, sortKey, descending) = Order(execution, sort, desc);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(page ?? 1, 1);
        var slice = order.Skip((number - 1) * size).Take(size).ToList();
        var keys = data.RowKeys is { } rowKeys && rowKeys.Count == data.Rows.Count ? slice.Select(i => rowKeys[i]).ToList() : null;
        var patients = data.RowPatientIds is { } ids && ids.Count == data.Rows.Count
            ? slice.Select(i => ids[i]).OfType<Guid>().Distinct().ToList()
            : [];

        var result = new ReportResultDto(
            execution.Report.Id,
            execution.Report.Title,
            execution.Report.Category,
            execution.Report.HasPeriod ? execution.Arguments.From : null,
            execution.Report.HasPeriod ? execution.Arguments.To : null,
            execution.Facility.Name,
            filters,
            execution.Arguments.ToCanonical(),
            execution.Columns,
            slice.Select(i => data.Rows[i]).ToList(),
            keys,
            data.Totals,
            data.Summary,
            ReportShaping.Chart(execution.Columns, data.Rows, data.Chart ?? execution.Report.Chart, chart, data.RowKeys),
            keys is null ? null : data.DrillDown,
            Notes(execution),
            number,
            size,
            data.Rows.Count,
            data.Truncated,
            sortKey,
            descending,
            execution.GeneratedAt,
            execution.DataSource,
            execution.Report.ContainsPatientIdentifiers,
            ReportPdf.Notice(data.Rows.Count));
        return (result, patients);
    }

    /// <summary>The whole report, sorted as on screen, ready for Excel, CSV or PDF.</summary>
    public ReportDocument Document(ReportExecution execution, IReadOnlyList<ReportFilterDto> filters, string generatedBy, string? sort, bool? desc)
    {
        var (order, _, _) = Order(execution, sort, desc);
        var data = execution.Data;
        var stamp = execution.Report.HasPeriod
            ? execution.Arguments.From == execution.Arguments.To
                ? execution.Arguments.From.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : $"{execution.Arguments.From:yyyyMMdd}-{execution.Arguments.To:yyyyMMdd}"
            : AnalyticsClock.LocalDate(execution.GeneratedAt).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new ReportDocument(
            execution.Report.Id,
            stamp,
            execution.Facility.IsConsolidated ? platform.Value.DefaultFacilityName : execution.Facility.Letterhead,
            execution.Report.Title,
            execution.Report.Definition,
            filters,
            generatedBy,
            execution.GeneratedAt,
            execution.DataSource,
            execution.Columns,
            order.Select(i => data.Rows[i]).ToList(),
            data.Totals,
            data.Summary,
            Notes(execution),
            execution.Report.ContainsPatientIdentifiers)
        {
            FacilityId = execution.Facility.FacilityId,
            RowPatientIds = data.RowPatientIds is { } ids && ids.Count == data.Rows.Count ? order.Select(i => ids[i]).ToList() : null,
        };
    }

    /// <summary>Patients listed anywhere in the report (exports are audited for all of them).</summary>
    public static IReadOnlyList<Guid> AllPatients(ReportExecution execution) =>
        execution.Data.RowPatientIds?.OfType<Guid>().Distinct().ToList() ?? [];

    /// <summary>
    /// Patients listed in an export file of <paramref name="rows"/> rows, which the export is audited for: every patient
    /// of the report, or only those in the first rows when the file holds part of it (the PDF of a very long list).
    /// When rows and patients cannot be matched, every patient of the report, so the audit never leaves one out.
    /// </summary>
    public static IReadOnlyList<Guid> PatientsIn(ReportExecution execution, ReportDocument document, int rows) =>
        rows < document.Rows.Count && document.RowPatientIds is { } ids
            ? ids.Take(rows).OfType<Guid>().Distinct().ToList()
            : AllPatients(execution);

    private static (IReadOnlyList<int> Order, string? SortKey, bool Descending) Order(ReportExecution execution, string? sort, bool? desc)
    {
        var data = execution.Data;
        var requested = !string.IsNullOrWhiteSpace(sort) && execution.Columns.Any(c => c.Key == sort) ? sort : null;
        var sortKey = requested ?? data.DefaultSort ?? execution.Report.DefaultSort;
        var descending = requested is not null ? desc ?? false : data.DefaultSortDescending ?? execution.Report.DefaultSortDescending;
        var index = -1;
        for (var i = 0; i < execution.Columns.Count; i++)
        {
            if (execution.Columns[i].Key == sortKey)
            {
                index = i;
                break;
            }
        }

        return (ReportShaping.SortOrder(data.Rows, index, descending), index < 0 ? null : sortKey, descending);
    }

    private static IReadOnlyList<string> Notes(ReportExecution execution) =>
        execution.Data.Truncated
            ? [.. execution.Data.Notes, "Only part of the data is shown because the report reached its row limit. Choose a shorter period or add a filter."]
            : execution.Data.Notes;

    private async Task<string?> NameAsync(ReportParameterKind kind, Guid id, CancellationToken ct) => kind switch
    {
        ReportParameterKind.Department => await db.Set<Department>().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(ct),
        ReportParameterKind.Practitioner => await db.Set<Practitioner>().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.DisplayName).FirstOrDefaultAsync(ct),
        ReportParameterKind.Payer => await db.Set<Payer>().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(ct),
        ReportParameterKind.Ward => await db.Set<Ward>().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(ct),
        ReportParameterKind.Store => await db.Set<Store>().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.Name).FirstOrDefaultAsync(ct),
        ReportParameterKind.Theatre => await db.Set<Theatre>().IgnoreQueryFilters().Where(x => x.Id == id).Select(x => x.Code + " · " + x.Name).FirstOrDefaultAsync(ct),
        _ => null,
    };
}
