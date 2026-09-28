using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

/// <summary>
/// Maps a <c>List&lt;T&gt;</c> of small value rows (vitals chart, checklist entries, readings) to one <c>jsonb</c>
/// column. The rows are always read and written with their parent, so a child table would only add joins.
/// A value comparer based on the serialised JSON makes in-place list changes visible to change tracking.
/// Enums are written as names so the JSON stays readable in reports and survives enum reordering.
/// </summary>
internal static class JsonColumn
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    public static PropertyBuilder<List<T>> HasJsonRows<T>(this PropertyBuilder<List<T>> property)
    {
        var comparer = new ValueComparer<List<T>>(
            (a, b) => Serialize(a) == Serialize(b),
            v => Serialize(v).GetHashCode(StringComparison.Ordinal),
            v => Deserialize<T>(Serialize(v)));

        property.HasConversion(v => Serialize(v), v => Deserialize<T>(v), comparer).HasColumnType("jsonb");
        return property;
    }

    internal static string Serialize<T>(List<T>? rows) => JsonSerializer.Serialize(rows ?? [], Options);

    internal static List<T> Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, Options) ?? [];

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
