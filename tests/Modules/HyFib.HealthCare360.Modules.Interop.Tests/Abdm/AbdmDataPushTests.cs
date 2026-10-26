using HyFib.HealthCare360.Modules.Interop.Abdm;
using HyFib.HealthCare360.Modules.Interop.Services;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>
/// Where the hospital sends a patient's encrypted records under a consent: only to an https address outside its own
/// network, whatever the request names; and how ABDM's timestamps are read.
/// </summary>
public sealed class AbdmDataPushTests
{
    [Theory]
    [InlineData("https://hiu.example.org/data/push", true)]
    [InlineData("https://203.0.113.7/push", true)]
    [InlineData("http://hiu.example.org/data/push", false)]
    [InlineData("https://user:pw@hiu.example.org/push", false)]
    [InlineData("https://localhost/push", false)]
    [InlineData("https://127.0.0.1/push", false)]
    [InlineData("https://10.1.2.3/push", false)]
    [InlineData("https://172.20.0.5/push", false)]
    [InlineData("https://192.168.1.10/push", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://100.64.0.1/push", false)]
    [InlineData("https://[::1]/push", false)]
    [InlineData("https://[fd00::1]/push", false)]
    [InlineData("https://[fe80::1]/push", false)]
    [InlineData("https://[::ffff:10.0.0.1]/push", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_an_https_address_outside_the_hospitals_network_takes_records(string? url, bool allowed) =>
        AbdmDataPush.IsAllowed(url, out _).ShouldBe(allowed);

    [Theory]
    [InlineData("2026-10-26T09:30:15.123Z", "2026-10-26T09:30:15.1230000+00:00")]
    [InlineData("2026-10-26T09:30:15.123", "2026-10-26T09:30:15.1230000+00:00")]
    [InlineData("2026-10-26T15:00:15+05:30", "2026-10-26T09:30:15.0000000+00:00")]
    public void Abdm_timestamps_without_an_offset_are_utc(string value, string expected) =>
        AbdmDataFlowService.Instant(value)!.Value.ToString("O", System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);

    [Fact]
    public void A_timestamp_that_is_not_one_is_no_date() => AbdmDataFlowService.Instant("soon").ShouldBeNull();
}
