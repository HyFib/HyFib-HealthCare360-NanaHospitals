namespace HyFib.HealthCare360.Modules.Analytics.Reports;

/// <summary>Catalogue sections, in the order management reviews them.</summary>
public enum ReportCategory
{
    OutPatient = 0,
    InPatient,
    Revenue,
    Diagnostics,
    Pharmacy,
    SpecialtyCare,
    Quality,
    Experience,
    People,
}

/// <summary>How a column's values are formatted (on screen, in Excel and in CSV).</summary>
public enum ReportValueType
{
    Text = 0,
    Integer,
    Decimal,

    /// <summary>Indian rupees, two decimals.</summary>
    Money,

    /// <summary>0–100 with one or two decimals.</summary>
    Percent,
    Days,
    Minutes,
    Date,
    DateTime,

    /// <summary>A calendar month, carried as its first day and shown as "Sep 2026".</summary>
    Month,
}

/// <summary>The input a report parameter asks for.</summary>
public enum ReportParameterKind
{
    /// <summary>A reporting period: the keys <c>period</c> (preset), <c>from</c> and <c>to</c>.</summary>
    Period = 0,
    Facility,
    Department,
    Practitioner,
    Payer,
    Ward,
    Store,
    Theatre,

    /// <summary>One value from a fixed list (<see cref="ReportParameter.Choices"/>).</summary>
    Choice,

    /// <summary>A whole number between <see cref="ReportParameter.Min"/> and <see cref="ReportParameter.Max"/>.</summary>
    Number,
}

/// <summary>Chart shapes the staff app renders (ECharts line, bar and pie), plus table-only and headline-figure views.</summary>
public enum ReportChartType
{
    Bar = 0,
    StackedBar,
    Line,
    Area,
    Pie,

    /// <summary>No chart: the table only.</summary>
    Table,

    /// <summary>The report's headline figures (dashboard number tiles).</summary>
    Summary,
}

public sealed record ReportChoice(string Value, string Label);

/// <summary>An input on the report form (and in a schedule or dashboard widget).</summary>
public sealed record ReportParameter(
    string Key,
    string Label,
    ReportParameterKind Kind,
    bool Required = false,
    string? DefaultValue = null,
    IReadOnlyList<ReportChoice>? Choices = null,
    int? Min = null,
    int? Max = null,
    string? Hint = null);

/// <summary>A result column. <see cref="IsPatientIdentifier"/> marks UHID/name columns of patient-level reports.</summary>
public sealed record ReportColumn(string Key, string Label, ReportValueType Type, bool IsPatientIdentifier = false);

/// <summary>
/// The default chart for a report: one category column (x axis or pie slices) and one or more numeric series.
/// Time series keep their chronological order; other charts show the <see cref="TopN"/> largest categories.
/// </summary>
public sealed record ReportChartHint(ReportChartType Type, string CategoryKey, IReadOnlyList<string> SeriesKeys, bool TimeSeries = false, int TopN = 15);

/// <summary>Everything the catalogue says about a report (the query lives in its <see cref="ReportDefinition"/>).</summary>
public sealed record ReportDescriptor(
    string Id,
    string Title,
    ReportCategory Category,
    string Description,
    string Definition,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<ReportParameter> Parameters,
    IReadOnlyList<ReportColumn> Columns,
    ReportChartHint? Chart,
    string? DefaultSort = null,
    bool DefaultSortDescending = false,
    bool ContainsPatientIdentifiers = false,
    int MaxDays = 366)
{
    /// <summary>Reports that list patients are never e-mailed; they are opened in HC360, where every view is audited.</summary>
    public bool CanSchedule => !ContainsPatientIdentifiers;

    public bool HasPeriod => Parameters.Any(p => p.Kind == ReportParameterKind.Period);

    public int ColumnIndex(string key)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (string.Equals(Columns[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>A headline figure shown above the table (and by "Summary" dashboard widgets).</summary>
public sealed record ReportFigure(string Key, string Label, object? Value, ReportValueType Type);

/// <summary>What a report produced: rows aligned with the descriptor's columns, totals and headline figures.</summary>
public sealed class ReportData
{
    public ReportData(IReadOnlyList<object?[]> rows) => Rows = rows;

    public IReadOnlyList<object?[]> Rows { get; }

    /// <summary>
    /// Columns for this run when the report's shape depends on its inputs (for example "show by department" or "by
    /// doctor"); null means the descriptor's columns.
    /// </summary>
    public IReadOnlyList<ReportColumn>? Columns { get; init; }

    /// <summary>Chart for this run when it differs from the descriptor's default; null means the default.</summary>
    public ReportChartHint? Chart { get; init; }

    /// <summary>Default sort column for this run (for example a ranking view of a report that is chronological by default).</summary>
    public string? DefaultSort { get; init; }

    public bool? DefaultSortDescending { get; init; }

    /// <summary>A totals row aligned with the columns (null cells are left blank); null when totals make no sense.</summary>
    public object?[]? Totals { get; init; }

    public IReadOnlyList<ReportFigure> Summary { get; init; } = [];

    /// <summary>Explanations shown under the report ("Beds in service: 120").</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Patients listed (patient-level reports only): each view or export is audited per patient.</summary>
    public IReadOnlyList<Guid> PatientIds { get; init; } = [];

    /// <summary>Set by the runner when a query hit the row cap.</summary>
    public bool Truncated { get; set; }
}

/// <summary>Standard parameter keys shared by the report form, schedules and dashboard widgets.</summary>
public static class ReportKeys
{
    public const string Period = "period";
    public const string From = "from";
    public const string To = "to";
    public const string Facility = "facilityId";
    public const string Department = "departmentId";
    public const string Practitioner = "practitionerId";
    public const string Payer = "payerId";
    public const string PayerType = "payerType";
    public const string Ward = "wardId";
    public const string Store = "storeId";
    public const string Theatre = "theatreId";
    public const string Days = "days";
    public const string GroupBy = "groupBy";
    public const string Section = "section";
    public const string Modality = "modality";
    public const string Outcome = "outcome";
    public const string VisitType = "visitType";

    /// <summary>Consolidated report across every facility (only for users allowed to see all of them).</summary>
    public const string AllFacilities = "all";
}
