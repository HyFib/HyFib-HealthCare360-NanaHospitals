using System.Reflection;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Numbering;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Administrators set number formats before the first number is issued, from the series the modules declare. Every
/// series a module defines is declared (or the server refuses to issue its numbers), and every default format follows
/// the rules an administrator's format must follow.
/// </summary>
public sealed class NumberSeriesDeclarationTests
{
    private static NumberSeriesCatalog Catalog => EndpointAuthorizationTests.App.Value.Services.GetRequiredService<NumberSeriesCatalog>();

    [Fact]
    public void Every_series_a_module_defines_is_declared()
    {
        var modules = EndpointAuthorizationTests.App.Value.Services.GetRequiredService<ModuleCatalog>();
        var defined = modules.Assemblies
            .SelectMany(LoadableTypes)
            .Where(type => !type.ContainsGenericParameters)
            .SelectMany(type => type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(p => p.PropertyType == typeof(NumberSeriesDefinition))
                .Select(p => (Name: $"{type.Name}.{p.Name}", Series: (NumberSeriesDefinition)p.GetValue(null)!))
                .Concat(type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(f => f.FieldType == typeof(NumberSeriesDefinition) && !f.Name.Contains('<', StringComparison.Ordinal))
                    .Select(f => (Name: $"{type.Name}.{f.Name}", Series: (NumberSeriesDefinition)f.GetValue(null)!))))
            .ToList();

        defined.Count.ShouldBeGreaterThan(50);
        defined.Where(d => !d.Series.Hidden && !Catalog.IsDeclared(d.Series)).Select(d => d.Name)
            .ShouldBeEmpty("Add these to their module's NumberSeries list, so administrators can set them up");
    }

    [Fact]
    public void Every_default_format_follows_the_rules()
    {
        var problems = Catalog.All
            .Where(s => !s.Hidden)
            .SelectMany(s => NumberFormatRules.FormatProblems(s.DefaultFormat)
                .Append(NumberFormatRules.ResetProblem(s.DefaultFormat, s.DefaultReset))
                .Append(NumberFormatRules.LongestNumber(s.DefaultFormat, NumberFormatRules.MaxFacilityCodeLength).Total > s.MaxLength
                    ? $"longer than {s.MaxLength} characters with a 6-letter facility code"
                    : null)
                .OfType<string>()
                .Select(problem => $"{s.Key}: {problem}"))
            .ToList();

        problems.ShouldBeEmpty("A default the administrator could not save would block \"Restore default\"");
    }

    [Fact]
    public void Every_series_says_what_it_is_and_why_a_locked_one_is_locked()
    {
        Catalog.All.Count.ShouldBeGreaterThan(50);
        Catalog.All.Where(s => string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Area) || s.MaxLength is < 1 or > 64)
            .Select(s => s.Key).ShouldBeEmpty();
        Catalog.All.Where(s => s.LockedReason is { } reason && !reason.EndsWith('.')).Select(s => s.Key)
            .ShouldBeEmpty("The locked reason is shown to administrators as a sentence");
        Catalog.All.Where(s => s.IsLocked).Select(s => s.Key).Order(StringComparer.Ordinal)
            .ShouldBe(["cardiology.accession", "lab.accession", "lab.sample", "radiology.accession", "uhid"]);
    }

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
