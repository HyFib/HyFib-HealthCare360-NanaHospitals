using System.Reflection;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// A module may reference only modules in lower layers; upward needs go through Platform contracts
/// (docs/architecture/ARCHITECTURE.md §4). Breaking this makes modules impossible to change independently.
/// </summary>
public sealed class LayeringTests
{
    private const string Prefix = "HyFib.HealthCare360.";

    private static readonly Dictionary<string, int> Layers = new(StringComparer.Ordinal)
    {
        ["SharedKernel"] = 0,
        ["WindowsInterop"] = 0,
        ["Platform"] = 1,

        // Adapters implement Platform abstractions; modules never reference them (see the test below).
        ["Integrations"] = 2,
        ["Modules.Patients"] = 2,
        ["Modules.SupplyChain"] = 2,
        ["Modules.HR"] = 2,
        ["Modules.Scheduling"] = 3,
        ["Modules.Clinical"] = 4,
        ["Modules.Diagnostics"] = 5,
        ["Modules.Pharmacy"] = 5,
        ["Modules.Inpatient"] = 5,
        ["Modules.VirtualCare"] = 5,
        ["Modules.SpecialtyCare"] = 6,
        ["Modules.SupportServices"] = 6,
        ["Modules.Engagement"] = 6,
        ["Modules.Quality"] = 6,
        ["Modules.Billing"] = 7,
        ["Modules.Analytics"] = 8,
        ["Modules.AI"] = 8,
    };

    public static TheoryData<string> Components() => [.. Layers.Keys];

    private static Assembly Load(string component) => Assembly.Load(Prefix + component);

    private static IEnumerable<string> InternalReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..]);

    [Theory]
    [MemberData(nameof(Components))]
    public void Components_only_reference_lower_layers(string component)
    {
        var layer = Layers[component];
        foreach (var reference in InternalReferences(Load(component)))
        {
            Layers.ShouldContainKey(reference, $"{component} references {reference}, which is not part of the layer map.");
            Layers[reference].ShouldBeLessThan(layer, $"{component} (layer {layer}) must not reference {reference} (layer {Layers[reference]}).");
        }
    }

    [Theory]
    [MemberData(nameof(Components))]
    public void Nothing_references_the_server_host(string component) =>
        InternalReferences(Load(component)).ShouldNotContain("Server");

    [Fact]
    public void Modules_reach_integrations_only_through_platform_abstractions()
    {
        foreach (var module in Layers.Keys.Where(k => k.StartsWith("Modules.", StringComparison.Ordinal)))
        {
            InternalReferences(Load(module)).ShouldNotContain("Integrations", $"{module} must use the Platform integration abstractions, not the adapters.");
        }
    }

    [Fact]
    public void Every_module_assembly_is_in_the_layer_map()
    {
        var server = Load("Server");
        var modules = InternalReferences(server).Where(r => r.StartsWith("Modules.", StringComparison.Ordinal)).ToList();

        modules.ShouldNotBeEmpty();
        foreach (var module in modules)
        {
            Layers.ShouldContainKey(module, $"Add {module} to the layer map (docs/architecture/ARCHITECTURE.md §4).");
        }
    }
}
