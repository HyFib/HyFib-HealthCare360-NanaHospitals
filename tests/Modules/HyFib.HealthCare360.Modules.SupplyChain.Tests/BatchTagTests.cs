using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class BatchTagTests
{
    [Theory]
    [InlineData("PCM500#AB1234", "PCM500", "AB1234")]
    [InlineData(" pcm500 # ab1234 ", "PCM500", "ab1234")]
    [InlineData("PH/AMOX-250#B 22/07", "PH/AMOX-250", "B 22/07")]
    [InlineData("PCM500#AB#2", "PCM500", "AB#2")]
    public void A_tag_names_the_item_code_and_the_batch(string scanned, string code, string batch)
    {
        BatchTag.TryParse(scanned, out var itemCode, out var batchNumber).ShouldBeTrue();

        (itemCode, batchNumber).ShouldBe((code, batch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PCM500")]
    [InlineData("Paracetamol 500")]
    [InlineData("#AB1234")]
    [InlineData("PCM500#")]
    [InlineData("PCM 500#AB1234")]
    [InlineData("PCM500#0123456789012345678901234567890123456789X")]
    public void Anything_else_is_an_ordinary_search(string? scanned) =>
        BatchTag.TryParse(scanned, out _, out _).ShouldBeFalse();

    [Fact]
    public void A_stored_batch_matches_whatever_the_case()
    {
        BatchTag.Matches("AB1234", "ab1234").ShouldBeTrue();
        BatchTag.Matches(" AB1234", "AB1234 ").ShouldBeTrue();
        BatchTag.Matches("AB1234", "AB1235").ShouldBeFalse();
    }
}
