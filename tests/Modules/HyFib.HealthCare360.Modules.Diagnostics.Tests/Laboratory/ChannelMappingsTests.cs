using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Laboratory;

/// <summary>
/// A channel is mapped when a test maps it by a plain code (any analyser) or by the analyser's own code; another
/// analyser's own code does not map it. The list of channels no test maps says how to write each one's mapping, the
/// analyser's own code first, and says so when its characters cannot be written as a mapping at all.
/// </summary>
public sealed class ChannelMappingsTests
{
    private static readonly string[][] Catalogue =
    [
        ["GLU"],
        ["XN550:HGB", "HB"],
        ["C150:CA"],
    ];

    [Theory]
    [InlineData("C150", "GLU", true)]
    [InlineData("C150", "glu", true)]
    [InlineData("XN550", "HGB", true)]
    [InlineData("H500", "HB", true)]
    [InlineData("C150", "CA", true)]
    [InlineData("c150", "ca", true)]
    [InlineData("H500", "HGB", false)]
    [InlineData("EXIAS", "CA", false)]
    [InlineData("C150", "CA2", false)]
    [InlineData("C150", "PHOS", false)]
    public void A_channel_is_mapped_by_a_plain_code_or_the_analysers_own(string analyser, string channel, bool mapped) =>
        ChannelMappings.MapsAny(Catalogue, analyser, channel).ShouldBe(mapped);

    [Fact]
    public void The_analysers_own_code_is_preferred_to_a_plain_one()
    {
        ChannelMappings.Score(["HGB", "XN550:HGB"], "XN550", "HGB").ShouldBe(2);
        ChannelMappings.Score(["HGB", "XN550:HGB"], "H500", "HGB").ShouldBe(1);
        ChannelMappings.Score(["XN550:HGB"], "H500", "HGB").ShouldBe(0);
    }

    [Fact]
    public void An_empty_catalogue_maps_nothing() =>
        ChannelMappings.MapsAny([], "C150", "GLU").ShouldBeFalse();

    [Theory]
    [InlineData("C150", "CA", "C150:CA")]
    [InlineData("H500", "RDW-CV", "H500:RDW-CV")]
    [InlineData("H500", "NEU#", "H500:NEU#")]
    [InlineData("H500", "LYM%", "H500:LYM%")]
    [InlineData("C150", "NA/K", null)]
    [InlineData("C150", "CA 2", "C150:CA 2")]
    [InlineData("ICHROMA", "VIT D-25", "ICHROMA:VIT D-25")]
    [InlineData("C150", "CA  2", null)]
    [InlineData("C150", "A:B", null)]
    public void The_mapping_to_write_is_the_analysers_own_when_it_can_be_written(string analyser, string channel, string? mapping) =>
        LabMasterEndpoints.MappingOf(analyser, channel).ShouldBe(mapping);

    /// <summary>
    /// Some analysers name a test with spaces in it, as the i-CHROMA does vitamin D ("VIT D-25", "VIT 25 OH"): a mapping may
    /// hold single spaces inside, never at an end, doubled, or beside the ':'.
    /// </summary>
    [Theory]
    [InlineData("VIT D-25", true)]
    [InlineData("VIT 25 OH", true)]
    [InlineData("ICHROMA:VIT D-25", true)]
    [InlineData(" VITD", false)]
    [InlineData("VITD ", false)]
    [InlineData("VIT  D", false)]
    [InlineData("ICHROMA :VITD", false)]
    [InlineData("ICHROMA: VITD", false)]
    [InlineData("VIT/D", false)]
    public void A_mapping_may_have_single_spaces_inside(string mapping, bool allowed) =>
        MasterRules.IsChannelMapping(mapping).ShouldBe(allowed);

    [Fact]
    public void A_channel_with_a_space_is_mapped_by_the_same_words()
    {
        string[] vitaminD = ["VITD", "VIT-D", "VIT D-25", "VIT-D3", "VIT D3", "VIT 25 OH"];

        ChannelMappings.Score(vitaminD, "ICHROMA", "VIT D-25").ShouldBe(1);
        ChannelMappings.Score(vitaminD, "ICHROMA", "vit 25 oh").ShouldBe(1);
        ChannelMappings.Score(vitaminD, "ICHROMA", "VITD25").ShouldBe(0);
    }

    [Fact]
    public void A_channel_too_long_for_the_analysers_own_mapping_is_written_plain()
    {
        var channel = new string('X', 60);
        LabMasterEndpoints.MappingOf("C150", channel).ShouldBe(channel);
        LabMasterEndpoints.MappingOf("C150", new string('X', 65)).ShouldBeNull();
    }
}
