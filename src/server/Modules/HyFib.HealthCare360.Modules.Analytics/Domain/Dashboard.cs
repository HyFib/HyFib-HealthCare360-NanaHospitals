using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Modules.Analytics.Reports;

namespace HyFib.HealthCare360.Modules.Analytics.Domain;

/// <summary>
/// A saved BI dashboard (BI-01): a grid of report widgets. Personal dashboards are visible to their owner; shared ones
/// to every analytics user; hospital templates (no owner) are seeded and can be edited by dashboard managers. Widgets
/// always run in the viewer's facility with the viewer's permissions, so sharing a dashboard never shares data a
/// colleague may not see.
/// </summary>
public sealed class Dashboard : AuditableEntity
{
    public const int MaxWidgets = 24;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Null for hospital templates.</summary>
    public Guid? OwnerUserId { get; set; }

    public string? OwnerName { get; set; }
    public bool IsShared { get; set; }

    /// <summary>The widgets as JSON (<see cref="DashboardLayout"/>).</summary>
    public string LayoutJson { get; set; } = "[]";

    public bool IsTemplate => OwnerUserId is null;

    public bool IsVisibleTo(Guid userId) => IsShared || IsTemplate || OwnerUserId == userId;

    /// <summary>The owner edits their dashboards; templates are edited by anyone who manages dashboards.</summary>
    public bool IsEditableBy(Guid userId, bool canManage) => canManage && (IsTemplate || OwnerUserId == userId);
}

/// <summary>
/// One tile on a dashboard: a report, how to show it (chart type, table or headline figures), its width in a four-column
/// grid (1, 2 or 4) and its inputs in canonical form (relative periods such as "last30Days").
/// </summary>
public sealed record DashboardWidget(string Id, string ReportId, string? Title, ReportChartType View, int Width, IReadOnlyDictionary<string, string> Parameters);

/// <summary>Serialises dashboard widgets (camelCase, enums as text) for the jsonb layout column.</summary>
public static class DashboardLayout
{
    public static readonly int[] Widths = [1, 2, 4];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IReadOnlyList<DashboardWidget> widgets) => JsonSerializer.Serialize(widgets, Options);

    /// <summary>Reads a stored layout; an unreadable layout yields no widgets rather than an error page.</summary>
    public static IReadOnlyList<DashboardWidget> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<DashboardWidget>>(json, Options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
