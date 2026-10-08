using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

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
    public void A_tag_finds_its_item_by_code_and_an_item_whose_barcode_is_the_whole_text()
    {
        Item[] items =
        [
            new() { Code = "PCM500", Name = "Paracetamol 500 mg" },
            new() { Code = "ZX", Name = "Gauze roll", Barcode = "ZX#99" },
            new() { Code = "IRN100", Name = "Iron syrup", Barcode = "PCM500#AB1234" },
        ];

        items.AsQueryable().Search(" PCM500#AB1234 ").Select(i => i.Name).ShouldBe(["Paracetamol 500 mg", "Iron syrup"]);
        items.AsQueryable().Search("ZX#99").Select(i => i.Name).ShouldBe(["Gauze roll"]);
    }

    [Fact]
    public void A_stored_batch_matches_whatever_the_case()
    {
        BatchTag.Matches("AB1234", "ab1234").ShouldBeTrue();
        BatchTag.Matches(" AB1234", "AB1234 ").ShouldBeTrue();
        BatchTag.Matches("AB1234", "AB1235").ShouldBeFalse();
    }
}
