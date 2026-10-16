using HyFib.HealthCare360.Platform.Identity;
using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The server name WebAuthn keys are tied to (SRS UX-11): the name the page was opened by, when it is one of the
/// server's (its certificate's names, its computer name, localhost, the configured extra names), over HTTPS. WebAuthn
/// cannot use an IP address, so a page opened by one offers the PIN and the password only.
/// </summary>
public sealed class WebAuthnRelyingPartyTests
{
    private static readonly string[] ServerNames = ["hc360-srv.nanahospitals.lan", "10.0.0.5", "localhost"];

    [Theory]
    [InlineData("hc360-srv.nanahospitals.lan", 7360, "https://hc360-srv.nanahospitals.lan:7360")]
    [InlineData("HC360-SRV.NanaHospitals.lan", 7360, "https://hc360-srv.nanahospitals.lan:7360")]
    [InlineData("hc360-srv.nanahospitals.lan", null, "https://hc360-srv.nanahospitals.lan")]
    public void A_name_of_the_server_over_https_is_the_relying_party(string host, int? port, string origin)
    {
        var rp = WebAuthnUnlockService.RelyingPartyOf(Request("https", host, port), ServerNames);

        rp.ShouldNotBeNull();
        rp.Id.ShouldBe("hc360-srv.nanahospitals.lan");
        rp.Origin.ShouldBe(origin);
    }

    [Fact]
    public void The_computer_name_and_its_local_name_always_are()
    {
        var machine = Environment.MachineName.ToLowerInvariant();

        WebAuthnUnlockService.RelyingPartyOf(Request("https", machine, 7360), []).ShouldNotBeNull().Id.ShouldBe(machine);
        WebAuthnUnlockService.RelyingPartyOf(Request("https", machine + ".local", 7360), []).ShouldNotBeNull().Id.ShouldBe(machine + ".local");
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("[fe80::1]")]
    [InlineData("[::1]")]
    public void An_ip_address_cannot_be_one_even_on_the_certificate(string host) =>
        WebAuthnUnlockService.RelyingPartyOf(Request("https", host, 7360), ServerNames).ShouldBeNull();

    [Fact]
    public void A_name_that_is_not_the_servers_is_not_one() =>
        WebAuthnUnlockService.RelyingPartyOf(Request("https", "evil.example", 7360), ServerNames).ShouldBeNull();

    [Fact]
    public void Plain_http_is_refused_except_from_localhost()
    {
        WebAuthnUnlockService.RelyingPartyOf(Request("http", "hc360-srv.nanahospitals.lan", 7360), ServerNames).ShouldBeNull();
        WebAuthnUnlockService.RelyingPartyOf(Request("http", "localhost", 5173), []).ShouldNotBeNull().Origin.ShouldBe("http://localhost:5173");
    }

    private static HttpRequest Request(string scheme, string host, int? port)
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = scheme;
        http.Request.Host = port is { } p ? new HostString(host, p) : new HostString(host);
        return http.Request;
    }
}
