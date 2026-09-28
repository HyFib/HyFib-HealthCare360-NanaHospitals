using HyFib.HealthCare360.Modules.Diagnostics.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>QR codes printed on released reports: unguessable, and unambiguous when typed from paper.</summary>
public sealed class VerificationCodeTests
{
    [Fact]
    public void Codes_are_twenty_unambiguous_symbols_and_do_not_repeat()
    {
        var codes = Enumerable.Range(0, 500).Select(_ => VerificationCode.New()).ToList();

        codes.ShouldAllBe(c => c.Length == VerificationCode.Length && VerificationCode.IsWellFormed(c));
        codes.ShouldAllBe(c => !c.Contains('0') && !c.Contains('O') && !c.Contains('1') && !c.Contains('I'));
        codes.Distinct().Count().ShouldBe(codes.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABCDEFGHJKLMNPQRSTU")]
    [InlineData("ABCDEFGHJKLMNPQRSTUVW")]
    [InlineData("abcdefghjkmnpqrstuvw")]
    [InlineData("ABCDEFGHJKLMNPQRST0O")]
    [InlineData("ABCDEFGHJKLMNPQRST/'")]
    public void Anything_else_is_rejected_before_the_database_is_queried(string? code) =>
        VerificationCode.IsWellFormed(code).ShouldBeFalse();

    [Fact]
    public void A_printed_code_is_recognised() =>
        VerificationCode.IsWellFormed("ABCDEFGHJKLMNPQRSTUV").ShouldBeTrue();
}
