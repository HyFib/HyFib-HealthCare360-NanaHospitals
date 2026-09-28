using HyFib.HealthCare360.Modules.AI.Services.Assistant;

namespace HyFib.HealthCare360.Modules.AI.Tests;

public sealed class SpokenNumbersTests
{
    [Theory]
    [InlineData("2", 2)]
    [InlineData("14", 14)]
    [InlineData("two", 2)]
    [InlineData("Three", 3)]
    [InlineData("a", 1)]
    [InlineData("an", 1)]
    [InlineData("a couple of", 2)]
    [InlineData("few", 3)]
    [InlineData("twenty", 20)]
    [InlineData("twenty one", 21)]
    [InlineData("twenty-one", 21)]
    [InlineData("ninety nine", 99)]
    public void Reads_numbers_as_speech_recognisers_write_them(string text, int expected)
    {
        SpokenNumbers.TryParse(text, out var value).ShouldBeTrue();

        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("several")]
    [InlineData("one hundred")]
    [InlineData("twenty zero")]
    [InlineData("twenty twelve")]
    public void Refuses_what_it_cannot_read_exactly(string? text)
    {
        SpokenNumbers.TryParse(text, out var value).ShouldBeFalse();

        value.ShouldBe(0);
    }
}
