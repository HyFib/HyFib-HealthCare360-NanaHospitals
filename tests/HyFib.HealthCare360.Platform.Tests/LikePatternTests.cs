using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The ILIKE patterns searches build: a term's own <c>%</c>, <c>_</c> and <c>\</c> are escaped with
/// <see cref="QueryExtensions.LikeEscape"/>, which each query passes to <c>EF.Functions.ILike</c> (LikeSearchTests run
/// them against PostgreSQL).
/// </summary>
public sealed class LikePatternTests
{
    [Theory]
    [InlineData("dr_meera", @"%dr\_meera%")]
    [InlineData("50%", @"%50\%%")]
    [InlineData(@"C:\temp", @"%C:\\temp%")]
    [InlineData("Asha", "%Asha%")]
    public void A_contains_pattern_escapes_the_terms_own_wildcards(string term, string pattern) =>
        term.ToLikePattern().ShouldBe(pattern);

    [Theory]
    [InlineData("pay_A1B2", @"pay\_A1B2")]
    [InlineData(@"a\_%", @"a\\\_\%")]
    public void A_literal_matches_only_itself(string term, string pattern) =>
        term.ToLikeLiteral().ShouldBe(pattern);

    [Fact]
    public void The_escape_character_is_the_backslash_the_patterns_use() =>
        QueryExtensions.LikeEscape.ShouldBe(@"\");
}
