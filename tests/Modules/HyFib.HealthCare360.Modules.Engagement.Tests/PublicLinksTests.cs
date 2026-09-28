using System.Text.Json;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Platform.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class PublicLinksTests
{
    [Theory]
    [InlineData("https://portal.nanahospitals.in", "https://portal.nanahospitals.in")]
    [InlineData("https://portal.nanahospitals.in/", "https://portal.nanahospitals.in")]
    [InlineData(" portal.nanahospitals.in ", "https://portal.nanahospitals.in")]
    [InlineData("https://nanahospitals.in/portal/?x=1#top", "https://nanahospitals.in/portal")]
    public void The_base_address_is_normalised_to_https(string configured, string expected) =>
        PublicLinks.Normalize(configured).ShouldBe(expected);

    [Theory]
    [InlineData("http://portal.nanahospitals.in")]
    [InlineData("ftp://portal.nanahospitals.in")]
    [InlineData("https://admin:secret@portal.nanahospitals.in")]
    [InlineData("")]
    [InlineData(null)]
    public void Plain_text_or_credential_bearing_addresses_are_never_used(string? configured) =>
        PublicLinks.Normalize(configured).ShouldBeNull();

    [Fact]
    public void Links_are_built_under_the_portal_address()
    {
        PublicLinks.Feedback("https://p.in", "tok").ShouldBe("https://p.in/feedback/tok");
        PublicLinks.FeedbackForm("https://p.in", "tok").ShouldBe("https://p.in/feedback/form/tok");
        PublicLinks.OptOut("https://p.in", "tok").ShouldBe("https://p.in/unsubscribe/tok");
    }

    [Fact]
    public async Task The_setting_is_read_per_facility()
    {
        var facility = Guid.NewGuid();
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<JsonElement?>(PublicLinks.BaseUrlSetting, facility, Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.SerializeToElement("portal.nanahospitals.in"));

        (await new PublicLinks(settings).BaseUrlAsync(facility, TestContext.Current.CancellationToken)).ShouldBe("https://portal.nanahospitals.in");
    }

    [Fact]
    public async Task A_malformed_or_missing_setting_reads_as_not_configured()
    {
        var settings = Substitute.For<ISettingsService>();
        var links = new PublicLinks(settings);
        var ct = TestContext.Current.CancellationToken;

        settings.GetAsync<JsonElement?>(PublicLinks.BaseUrlSetting, null, Arg.Any<CancellationToken>()).Returns((JsonElement?)null);
        (await links.BaseUrlAsync(null, ct)).ShouldBeNull();

        settings.GetAsync<JsonElement?>(PublicLinks.BaseUrlSetting, null, Arg.Any<CancellationToken>()).Returns(JsonSerializer.SerializeToElement(42));
        (await links.BaseUrlAsync(null, ct)).ShouldBeNull();

        settings.GetAsync<JsonElement?>(PublicLinks.BaseUrlSetting, null, Arg.Any<CancellationToken>()).Throws(new JsonException("bad"));
        (await links.BaseUrlAsync(null, ct)).ShouldBeNull();
    }

    [Fact]
    public void Tokens_are_random_url_safe_and_stored_only_as_hashes()
    {
        var token = OpaqueToken.New();

        token.Length.ShouldBe(22);
        OpaqueToken.IsWellFormed(token).ShouldBeTrue();
        OpaqueToken.New().ShouldNotBe(token);
        OpaqueToken.Hash(token).ShouldBe(OpaqueToken.Hash(token));
        OpaqueToken.Hash(token).Length.ShouldBe(64);
        OpaqueToken.Hash(token).ShouldNotContain(token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("abc/def+ghi=jkl0123456")]
    [InlineData("' OR 1=1 --xxxxxxxxxxxx")]
    public void Malformed_tokens_are_rejected_before_any_lookup(string? token) =>
        OpaqueToken.IsWellFormed(token).ShouldBeFalse();
}
