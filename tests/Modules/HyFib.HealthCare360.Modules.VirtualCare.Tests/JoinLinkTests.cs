using HyFib.HealthCare360.Modules.VirtualCare.Services;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

/// <summary>The patient's join link is a page of the patient portal under the hospital's public https address.</summary>
public sealed class JoinLinkTests
{
    private static readonly Guid Session = Guid.Parse("0199b000-0000-7000-8000-000000000001");

    [Fact]
    public void The_link_opens_the_portal_telehealth_page_with_the_token() =>
        TelehealthLinkSender.JoinUrl("https://care.nanahospitals.in", Session, "abc-DEF_123")
            .ShouldBe($"https://care.nanahospitals.in/portal/telehealth/{Session}?t=abc-DEF_123");

    [Fact]
    public void Without_a_public_address_the_link_is_server_relative() =>
        TelehealthLinkSender.JoinUrl(null, Session, "tok").ShouldBe($"/portal/telehealth/{Session}?t=tok");

    [Fact]
    public void The_token_is_escaped() =>
        TelehealthLinkSender.JoinUrl("https://care.nanahospitals.in", Session, "a+b/c=").ShouldEndWith("?t=a%2Bb%2Fc%3D");

    [Fact]
    public void A_setting_that_includes_the_portal_path_gives_the_same_link() =>
        TelehealthLinkSender.JoinUrl(PortalAddress.Normalize("https://care.nanahospitals.in/portal/"), Session, "tok")
            .ShouldBe($"https://care.nanahospitals.in/portal/telehealth/{Session}?t=tok");
}
