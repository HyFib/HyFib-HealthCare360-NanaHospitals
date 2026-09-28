using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// Schema ids in the OpenAPI document default to the bare type name, so records with the same name in two modules (for
/// example Billing's and the canteen's <c>SettlementResult</c>) would collide. Only names used in more than one namespace
/// get their module as a prefix ("BillingSettlementResult"), so ids stay short and identical from build to build.
/// </summary>
internal static class OpenApiSchemaIds
{
    private const string Prefix = "HyFib.HealthCare360.";

    private static readonly Lazy<HashSet<string>> SharedNames = new(FindSharedNames);

    public static string? Create(JsonTypeInfo typeInfo)
    {
        var id = OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
        var type = typeInfo.Type;
        if (id is null || type.Namespace is not { } ns || !ns.StartsWith(Prefix, StringComparison.Ordinal) || !SharedNames.Value.Contains(type.Name))
        {
            return id;
        }

        return ModuleOf(ns) + id;
    }

    /// <summary>"HyFib.HealthCare360.Modules.Billing.Features.Inpatient" → "Billing"; "HyFib.HealthCare360.Platform.Identity" → "Platform".</summary>
    internal static string ModuleOf(string ns)
    {
        var parts = ns[Prefix.Length..].Split('.');
        return parts[0] == "Modules" && parts.Length > 1 ? parts[1] : parts[0];
    }

    private static HashSet<string> FindSharedNames() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .SelectMany(LoadableTypes)
            .Where(t => t.Namespace is not null && !t.Name.StartsWith('<'))
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Select(t => t.Namespace).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }
}
