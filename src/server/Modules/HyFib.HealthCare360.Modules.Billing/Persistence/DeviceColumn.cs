using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

/// <summary>
/// Maps the <see cref="DeviceDetails"/> of a charge or bill line to one <c>jsonb</c> column: a handful of printed facts
/// read and written with their line, null for a service. The details are an immutable record, so record equality is
/// what change tracking compares.
/// </summary>
internal static class DeviceColumn
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<DeviceDetails?> HasDeviceJson(this PropertyBuilder<DeviceDetails?> property)
    {
        var comparer = new ValueComparer<DeviceDetails?>((a, b) => Equals(a, b), v => v == null ? 0 : v.GetHashCode(), v => v);
        property.HasConversion(v => Serialize(v), v => Deserialize(v), comparer).HasColumnType("jsonb");
        return property;
    }

    private static string Serialize(DeviceDetails? device) => JsonSerializer.Serialize(device, Options);

    private static DeviceDetails? Deserialize(string json) => JsonSerializer.Deserialize<DeviceDetails>(json, Options);
}
