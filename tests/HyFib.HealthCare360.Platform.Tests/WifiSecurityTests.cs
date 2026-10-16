using HyFib.HealthCare360.WindowsInterop;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS DED-12: administrators are warned when the Server Node is on an open, WEP or WPA-Personal network. Open and WEP
/// are insecure outright; WPA-Personal (WPA3's SAE included) is encrypted but shares one password among everyone on it.
/// </summary>
public sealed class WifiSecurityTests
{
    [Theory]
    [InlineData(WifiSecurityLevel.Open, true, false)]
    [InlineData(WifiSecurityLevel.Wep, true, false)]
    [InlineData(WifiSecurityLevel.Personal, false, true)]
    [InlineData(WifiSecurityLevel.Wpa3Personal, false, true)]
    [InlineData(WifiSecurityLevel.Enterprise, false, false)]
    [InlineData(WifiSecurityLevel.Wpa3Enterprise, false, false)]
    [InlineData(WifiSecurityLevel.Unknown, false, false)]
    public void Each_security_level_is_warned_about_as_DED_12_asks(WifiSecurityLevel security, bool insecure, bool sharedPassword)
    {
        var link = Link(security);

        link.IsInsecure.ShouldBe(insecure);
        link.UsesSharedPassword.ShouldBe(sharedPassword);
    }

    [Fact]
    public void Nothing_is_said_about_a_network_whose_details_Windows_withholds()
    {
        var link = Link(WifiSecurityLevel.Personal) with { DetailsAvailable = false };

        link.IsInsecure.ShouldBeFalse();
        link.UsesSharedPassword.ShouldBeFalse();
    }

    private static WifiConnectionInfo Link(WifiSecurityLevel security) =>
        new(Guid.NewGuid(), "Wireless adapter", true, "Staff", "00:11:22:33:44:55", 80, -55, 300, 300, "ax", null, null, security);
}
