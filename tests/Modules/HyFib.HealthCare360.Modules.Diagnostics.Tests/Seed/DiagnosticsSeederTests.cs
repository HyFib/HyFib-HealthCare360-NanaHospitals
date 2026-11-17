using HyFib.HealthCare360.Modules.Diagnostics.Seed;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Seed;

/// <summary>
/// A database seeded before the CBC gained the Yumizen H500's parameters gets them on its CBC when it is upgraded: each new
/// analyte joins the panel in the seed's place, and the panel's own analytes keep their order.
/// </summary>
public sealed class DiagnosticsSeederTests
{
    private static readonly string[] CbcBefore = ["HB", "TLC", "NEUT", "LYMPH", "MONO", "EOS", "BASO", "RBC", "PCV", "MCV", "MCH", "MCHC", "PLT"];

    private static IReadOnlyList<string> CbcNow => [.. LabCatalog.Orderables.Single(o => o.Code == "LAB-CBC").Components.Select(c => c.Code)];

    [Fact]
    public void The_new_parameters_join_a_cbc_seeded_before_them_in_the_seeds_order()
    {
        var added = CbcNow.Except(CbcBefore).ToList();

        added.Count.ShouldBe(13);
        DiagnosticsSeeder.PanelOrder(CbcBefore, CbcNow, added).ShouldBe(CbcNow);
    }

    [Fact]
    public void A_panel_the_lab_rearranged_keeps_its_order_and_each_new_analyte_follows_the_nearest_one_before_it_in_the_seed()
    {
        // The lab moved the platelets up and took out monocytes and basophils.
        string[] edited = ["HB", "PLT", "TLC", "NEUT", "LYMPH", "EOS", "RBC", "PCV", "MCV", "MCH", "MCHC"];

        var order = DiagnosticsSeeder.PanelOrder(edited, CbcNow, ["MPV", "IG", "BASO-ABS", "RDW-CV"]);

        order.ShouldBe(["HB", "PLT", "MPV", "TLC", "NEUT", "LYMPH", "EOS", "IG", "BASO-ABS", "RBC", "PCV", "MCV", "MCH", "MCHC", "RDW-CV"]);
    }

    [Fact]
    public void An_analyte_the_panel_has_already_is_not_added_twice_and_one_with_nothing_before_it_goes_first()
    {
        DiagnosticsSeeder.PanelOrder(["B", "C"], ["A", "B", "C"], ["B"]).ShouldBe(["B", "C"]);
        DiagnosticsSeeder.PanelOrder(["B", "C"], ["A", "B", "C"], ["A"]).ShouldBe(["A", "B", "C"]);
    }
}
