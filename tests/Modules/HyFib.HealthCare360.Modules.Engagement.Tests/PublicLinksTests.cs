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
    [InlineData("https://care.nanahospitals.in", "https://care.nanahospitals.in")]
    [InlineData("https://care.nanahospitals.in/", "https://care.nanahospitals.in")]
    [InlineData(" care.nanahospitals.in ", "https://care.nanahospitals.in")]
    [InlineData("https://care.nanahospitals.in/portal/?x=1#top", "https://care.nanahospitals.in")]
    public void The_base_address_is_the_hospitals_https_origin(string configured, string expected) =>
        PublicLinks.Normalize(configured).ShouldBe(expected);

    [Theory]
    [InlineData("http://care.nanahospitals.in")]
    [InlineData("ftp://care.nanahospitals.in")]
    [InlineData("https://admin:secret@care.nanahospitals.in")]
    [InlineData("")]
    [InlineData(null)]
    public void Plain_text_or_credential_bearing_addresses_are_never_used(string? configured) =>
        PublicLinks.Normalize(configured).ShouldBeNull();

    [Fact]
    public void Links_are_pages_of_the_patient_portal()
    {
        PublicLinks.Feedback("https://p.in", "tok").ShouldBe("https://p.in/portal/feedback/tok");
        PublicLinks.FeedbackForm("https://p.in", "tok").ShouldBe("https://p.in/portal/feedback/form/tok");
        PublicLinks.OptOut("https://p.in", "tok").ShouldBe("https://p.in/portal/unsubscribe/tok");
        PublicLinks.PortalHome("https://p.in").ShouldBe("https://p.in/portal/");
    }

    [Theory]
    [InlineData("care.nanahospitals.in")]
    [InlineData("https://care.nanahospitals.in/portal")]
    public async Task The_setting_is_read_per_facility_and_either_spelling_gives_the_same_links(string configured)
    {
        var facility = Guid.NewGuid();
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<JsonElement?>(PublicLinks.BaseUrlSetting, facility, Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.SerializeToElement(configured));

        var baseUrl = await new PublicLinks(settings).BaseUrlAsync(facility, TestContext.Current.CancellationToken);

        baseUrl.ShouldBe("https://care.nanahospitals.in");
        PublicLinks.Feedback(baseUrl!, "tok").ShouldBe("https://care.nanahospitals.in/portal/feedback/tok");
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
