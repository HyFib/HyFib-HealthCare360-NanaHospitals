using HyFib.HealthCare360.Modules.Diagnostics.Seed;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Seed;

/// <summary>
/// A database seeded before the CBC gained the Yumizen H500's parameters gets them on its CBC when it is upgraded: each new
/// analyte joins the panel in the seed's place, and the panel's own analytes keep their order. Once, it also gets the
/// channel codes tests it has gained since: the H500's for the differential and the names of vitamin D.
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

    [Fact]
    public void A_database_seeded_before_gets_the_h500_codes_first_and_the_vitamin_d_names_after_its_own()
    {
        var taken = new HashSet<string>(["NEUT%", "VITD"], StringComparer.OrdinalIgnoreCase);

        DiagnosticsSeeder.WithChannels(["NEUT%"], ["NEU%"], first: true, taken).ShouldBe(["NEU%", "NEUT%"]);
        DiagnosticsSeeder.WithChannels(["VITD"], ["VIT-D", "VIT D-25"], first: false, taken).ShouldBe(["VITD", "VIT-D", "VIT D-25"]);
    }

    [Fact]
    public void A_code_a_test_maps_already_or_the_lab_ignored_is_not_added()
    {
        // NEU% mapped by the lab on a test of its own (any case, or as an analyser's own code), VIT D3 ignored by the lab.
        var taken = new HashSet<string>(["neu%", "VIT D3"], StringComparer.OrdinalIgnoreCase);

        DiagnosticsSeeder.WithChannels(["NEUT%"], ["NEU%"], first: true, taken).ShouldBe(["NEUT%"]);
        DiagnosticsSeeder.WithChannels(["VITD"], ["VIT-D3", "VIT D3"], first: false, taken).ShouldBe(["VITD", "VIT-D3"]);
    }

    [Fact]
    public void The_codes_added_to_a_database_from_before_are_the_seeds_own_so_a_new_database_has_them_already()
    {
        foreach (var (code, channels, first) in DiagnosticsSeeder.ChannelsAdded)
        {
            var seeded = (LabCatalog.Analytes.SingleOrDefault(a => a.Code == code)?.Result
                          ?? LabCatalog.Orderables.Single(o => o.Code == code).Result!).Channels!;
            channels.ShouldAllBe(c => seeded.Contains(c), code);
            if (first)
            {
                seeded.Take(channels.Length).ShouldBe(channels, code);
            }
        }

        // The analyte whose creation marks a database from before them is in the seed.
        LabCatalog.Analytes.ShouldContain(a => a.Code == DiagnosticsSeeder.ChannelsAddedWith);
    }
}
