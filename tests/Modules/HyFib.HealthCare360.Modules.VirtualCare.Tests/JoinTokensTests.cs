using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class JoinTokensTests
{
    private static readonly Guid Session = Guid.Parse("0199b000-0000-7000-8000-000000000001");
    private static readonly Guid OtherSession = Guid.Parse("0199b000-0000-7000-8000-000000000002");

    [Fact]
    public void New_tokens_are_random_long_and_url_safe()
    {
        var first = JoinTokens.NewToken();
        var second = JoinTokens.NewToken();

        first.ShouldNotBe(second);
        first.Length.ShouldBeGreaterThanOrEqualTo(43);
        first.ShouldAllBe(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');
    }

    [Fact]
    public void The_stored_hash_is_deterministic_and_never_contains_the_token()
    {
        var token = JoinTokens.NewToken();

        var hash = JoinTokens.Hash(Session, token);

        hash.Length.ShouldBe(64);
        hash.ShouldNotContain(token);
        JoinTokens.Hash(Session, token).ShouldBe(hash);
    }

    [Fact]
    public void The_issued_token_matches_its_hash()
    {
        var token = JoinTokens.NewToken();

        JoinTokens.Matches(Session, token, JoinTokens.Hash(Session, token)).ShouldBeTrue();
    }

    [Fact]
    public void An_altered_token_does_not_match()
    {
        var token = JoinTokens.NewToken();
        var hash = JoinTokens.Hash(Session, token);
        var altered = (token[0] == 'A' ? 'B' : 'A') + token[1..];

        JoinTokens.Matches(Session, altered, hash).ShouldBeFalse();
        JoinTokens.Matches(Session, token + "x", hash).ShouldBeFalse();
    }

    [Fact]
    public void A_token_can_never_open_another_session()
    {
        var token = JoinTokens.NewToken();

        JoinTokens.Matches(OtherSession, token, JoinTokens.Hash(Session, token)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Missing_tokens_never_match(string? token) =>
        JoinTokens.Matches(Session, token, JoinTokens.Hash(Session, "anything")).ShouldBeFalse();

    [Fact]
    public void A_session_without_a_stored_hash_accepts_no_token() =>
        JoinTokens.Matches(Session, JoinTokens.NewToken(), null).ShouldBeFalse();

    [Fact]
    public void Oversized_tokens_are_rejected()
    {
        var huge = new string('a', JoinTokens.MaxTokenLength + 1);

        JoinTokens.Matches(Session, huge, JoinTokens.Hash(Session, huge)).ShouldBeFalse();
    }
}
