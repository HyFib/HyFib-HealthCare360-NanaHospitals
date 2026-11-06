using System.Text.Json;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// The unit an analyser sent is the test's when it is the same unit written another way, never when it is another
/// quantity or scale. The pairs are the staff app's too (src/web/apps/staff/src/modules/lab/unitSpellings.json, read
/// by flags.test.ts), so the server's auto-validation and the app's review hints read units alike.
/// </summary>
public sealed class AnalyserUnitsTests
{
    public static TheoryData<string?, string?> Same => Pairs("same");

    public static TheoryData<string?, string?> Different => Pairs("different");

    [Theory]
    [MemberData(nameof(Same))]
    public void The_same_unit_written_another_way_is_the_tests_unit(string? analyser, string? test) =>
        AnalyserUnits.AreSame(analyser, test).ShouldBeTrue($"{analyser} against {test}");

    [Theory]
    [MemberData(nameof(Different))]
    public void A_unit_of_another_quantity_or_scale_is_not(string? analyser, string? test) =>
        AnalyserUnits.AreSame(analyser, test).ShouldBeFalse($"{analyser} against {test}");

    [Fact]
    public void A_white_cell_count_in_another_spelling_is_auto_validated_and_one_in_another_scale_waits()
    {
        var clean = new AutoValidationInput(LabResultType.Numeric, ResultFlag.Normal, true, true, true, DeltaOutcome.None, null);

        AutoValidationPolicy.Evaluate(clean with { InstrumentUnit = "10^9/L", TestUnit = "10³/µL" }).Approved.ShouldBeTrue();
        AutoValidationPolicy.Evaluate(clean with { InstrumentUnit = "10^6/uL", TestUnit = "10³/µL" }).Reasons
            .ShouldBe(["The analyser sent the value in 10^6/uL, not in the test's unit, 10³/µL."]);
    }

    private static TheoryData<string?, string?> Pairs(string kind)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "web", "apps", "staff", "src", "modules", "lab", "unitSpellings.json")));
        var data = new TheoryData<string?, string?>();
        foreach (var pair in document.RootElement.GetProperty(kind).EnumerateArray())
        {
            data.Add(pair[0].GetString(), pair[1].GetString());
        }

        return data;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HyFib.HealthCare360.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (HyFib.HealthCare360.slnx) was not found above the test's folder.");
    }
}
