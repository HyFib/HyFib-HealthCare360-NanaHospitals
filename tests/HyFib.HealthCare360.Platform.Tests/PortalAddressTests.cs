using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// One convention for every patient-facing link: <c>portal.baseUrl</c> is the hospital's public https origin and
/// patient links are <c>{base}/portal/...</c>. Links carry personal tokens, so anything but https is refused.
/// </summary>
public sealed class PortalAddressTests
{
    [Theory]
    [InlineData("https://care.nanahospitals.in", "https://care.nanahospitals.in")]
    [InlineData("https://care.nanahospitals.in/", "https://care.nanahospitals.in")]
    [InlineData(" care.nanahospitals.in ", "https://care.nanahospitals.in")]
    [InlineData("HTTPS://Care.NanaHospitals.in", "https://care.nanahospitals.in")]
    [InlineData("https://care.nanahospitals.in:8443/", "https://care.nanahospitals.in:8443")]
    [InlineData("https://care.nanahospitals.in/?x=1#top", "https://care.nanahospitals.in")]
    public void The_address_is_normalised_to_an_https_origin(string configured, string expected) =>
        PortalAddress.Normalize(configured).ShouldBe(expected);

    [Theory]
    [InlineData("https://care.nanahospitals.in/portal")]
    [InlineData("https://care.nanahospitals.in/portal/")]
    [InlineData("https://care.nanahospitals.in/Portal")]
    [InlineData("care.nanahospitals.in/portal")]
    [InlineData("https://care.nanahospitals.in/portal/?x=1#top")]
    public void A_trailing_portal_segment_is_dropped_so_either_spelling_works(string configured) =>
        PortalAddress.Normalize(configured).ShouldBe("https://care.nanahospitals.in");

    [Theory]
    [InlineData("https://nanahospitals.in/hc360", "https://nanahospitals.in/hc360")]
    [InlineData("https://nanahospitals.in/hc360/portal", "https://nanahospitals.in/hc360")]
    [InlineData("https://nanahospitals.in/myportal", "https://nanahospitals.in/myportal")]
    public void A_path_prefix_of_a_reverse_proxy_is_kept(string configured, string expected) =>
        PortalAddress.Normalize(configured).ShouldBe(expected);

    [Theory]
    [InlineData("http://care.nanahospitals.in")]
    [InlineData("http://care.nanahospitals.in/portal")]
    [InlineData("ftp://care.nanahospitals.in")]
    [InlineData("https://admin:secret@care.nanahospitals.in")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Plain_text_credential_bearing_or_malformed_addresses_are_never_used(string? configured) =>
        PortalAddress.Normalize(configured).ShouldBeNull();

    [Fact]
    public void Pages_live_under_the_portal_path()
    {
        PortalAddress.Page("https://care.nanahospitals.in", "feedback/tok").ShouldBe("https://care.nanahospitals.in/portal/feedback/tok");
        PortalAddress.Page("https://care.nanahospitals.in", "/verify/lab/ABC").ShouldBe("https://care.nanahospitals.in/portal/verify/lab/ABC");
        PortalAddress.Page("https://care.nanahospitals.in", string.Empty).ShouldBe("https://care.nanahospitals.in/portal/");
    }

    [Fact]
    public void An_empty_base_gives_the_server_relative_path() =>
        PortalAddress.Page(string.Empty, "telehealth/42?t=x").ShouldBe("/portal/telehealth/42?t=x");

    [Fact]
    public void Either_spelling_of_the_setting_produces_the_same_link()
    {
        var withPortal = PortalAddress.Normalize("https://care.nanahospitals.in/portal")!;
        var withoutPortal = PortalAddress.Normalize("https://care.nanahospitals.in")!;

        PortalAddress.Page(withPortal, "doctors/7").ShouldBe(PortalAddress.Page(withoutPortal, "doctors/7"));
        PortalAddress.Page(withPortal, "doctors/7").ShouldBe("https://care.nanahospitals.in/portal/doctors/7");
    }

    [Fact]
    public async Task The_setting_is_read_per_facility()
    {
        var facility = Guid.NewGuid();
        var settings = Substitute.For<ISettingsService>();
        settings.GetAsync<JsonElement?>(PortalAddress.BaseUrlSetting, facility, Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.SerializeToElement("care.nanahospitals.in/portal/"));

        (await PortalAddress.BaseUrlAsync(settings, facility, TestContext.Current.CancellationToken)).ShouldBe("https://care.nanahospitals.in");
    }

    [Fact]
    public async Task A_missing_non_text_insecure_or_unreadable_setting_reads_as_not_configured()
    {
        var settings = Substitute.For<ISettingsService>();
        var ct = TestContext.Current.CancellationToken;

        settings.GetAsync<JsonElement?>(PortalAddress.BaseUrlSetting, null, Arg.Any<CancellationToken>()).Returns((JsonElement?)null);
        (await PortalAddress.BaseUrlAsync(settings, null, ct)).ShouldBeNull();

        settings.GetAsync<JsonElement?>(PortalAddress.BaseUrlSetting, null, Arg.Any<CancellationToken>()).Returns(JsonSerializer.SerializeToElement(42));
        (await PortalAddress.BaseUrlAsync(settings, null, ct)).ShouldBeNull();

        settings.GetAsync<JsonElement?>(PortalAddress.BaseUrlSetting, null, Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.SerializeToElement("http://care.nanahospitals.in"));
        (await PortalAddress.BaseUrlAsync(settings, null, ct)).ShouldBeNull();

        settings.GetAsync<JsonElement?>(PortalAddress.BaseUrlSetting, null, Arg.Any<CancellationToken>()).Throws(new JsonException("bad"));
        (await PortalAddress.BaseUrlAsync(settings, null, ct)).ShouldBeNull();
    }
}
