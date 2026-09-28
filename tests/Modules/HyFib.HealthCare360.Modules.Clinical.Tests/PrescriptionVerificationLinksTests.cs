using HyFib.HealthCare360.Modules.Clinical.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

/// <summary>The QR code of a printed prescription must open from any phone, not only on the hospital LAN.</summary>
public sealed class PrescriptionVerificationLinksTests
{
    private const string Token = "Zk3_9aB-qR7tLm2xWv5c";

    [Fact]
    public void The_url_is_the_portal_verification_page_under_the_public_address() =>
        PrescriptionVerificationLinks.Url("https://care.nanahospitals.in", Token)
            .ShouldBe($"https://care.nanahospitals.in/portal/verify/prescription/{Token}");

    [Fact]
    public void Without_a_public_address_there_is_no_url_but_the_api_path_stays()
    {
        PrescriptionVerificationLinks.Url(null, Token).ShouldBeNull();
        PrescriptionVerificationLinks.Path(Token).ShouldBe($"/api/v1/public/prescriptions/verify/{Token}");
    }

    [Fact]
    public void An_unsigned_prescription_has_neither()
    {
        PrescriptionVerificationLinks.Url("https://care.nanahospitals.in", null).ShouldBeNull();
        PrescriptionVerificationLinks.Path(null).ShouldBeNull();
    }
}
