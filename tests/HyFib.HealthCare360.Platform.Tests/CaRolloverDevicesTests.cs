using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// During a CA rollover, System health names the devices that have not taken the new CA (<see cref="CaRolloverDevices"/>),
/// from the CAs each reports in its heartbeat (<see cref="DeviceCaFingerprints"/>). The names read from the database are
/// tested over the real API (integration test CaRolloverDeviceTests); the wording and when it turns critical here.
/// </summary>
public sealed class CaRolloverDevicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_to_say_when_every_device_seen_took_the_new_CA() =>
        CaRolloverDevices.Warning(new CaRolloverReadiness([], [], 12), Now.AddDays(200), Now).ShouldBeNull();

    [Fact]
    public void Names_the_devices_on_the_previous_CA_and_those_not_saying_and_turns_critical_in_the_last_90_days()
    {
        var readiness = new CaRolloverReadiness(["Lab PC 2", "OPD Reception 1"], ["Ward 3 Station"], 12);

        var warning = CaRolloverDevices.Warning(readiness, Now.AddDays(200), Now).ShouldNotBeNull();

        warning.Code.ShouldBe("cert.rollover_devices");
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldStartWith(
            $"The hospital CA was rolled over: 3 of the 12 devices seen in the last 30 days have not taken the new CA, and stop trusting this server on {Now.AddDays(200):dd MMM yyyy}.");
        warning.Message.ShouldContain("On the previous CA: Lab PC 2, OPD Reception 1.");
        warning.Message.ShouldContain("Not saying which CA they trust (an HC360 Desktop or lab agent older than this server): Ward 3 Station.");
        CaRolloverDevices.Warning(readiness, Now.AddDays(91), Now)!.Severity.ShouldBe("warning");
        CaRolloverDevices.Warning(readiness, Now.AddDays(89), Now)!.Severity.ShouldBe("critical", "as the previous CA's own warning, in its last 90 days");
        CaRolloverDevices.Warning(new CaRolloverReadiness([], ["Ward 3 Station"], 1), Now.AddDays(89), Now)!.Severity
            .ShouldBe("critical", "a device that does not say may be cut off as well");
    }

    [Fact]
    public void Names_ten_devices_of_a_group_and_counts_the_others()
    {
        var many = Enumerable.Range(1, 13).Select(i => $"PC {i:00}").ToList();

        CaRolloverDevices.Warning(new CaRolloverReadiness(many, [], 13), Now.AddDays(200), Now)!.Message
            .ShouldContain("On the previous CA: PC 01, PC 02, PC 03, PC 04, PC 05, PC 06, PC 07, PC 08, PC 09, PC 10 and 3 more.");
    }

    [Theory]
    [InlineData(0, 64, true)]
    [InlineData(4, 64, true)]
    [InlineData(5, 64, false)]
    [InlineData(1, 63, false)]
    [InlineData(1, 65, false)]
    public void A_device_reports_at_most_four_SHA_256_fingerprints(int count, int length, bool valid) =>
        DeviceCaFingerprints.AreValid([.. Enumerable.Range(0, count).Select(i => new string((char)('A' + i), length))]).ShouldBe(valid);

    [Fact]
    public void Fingerprints_are_hexadecimal_and_stored_upper_case_once_each_in_the_order_reported()
    {
        DeviceCaFingerprints.AreValid(null).ShouldBeTrue("from a version that does not report them");
        DeviceCaFingerprints.AreValid([new string('G', 64)]).ShouldBeFalse();
        DeviceCaFingerprints.AreValid([new string('a', 63) + " "]).ShouldBeFalse();

        var current = new string('b', 64);
        var previous = new string('C', 64);
        var stored = DeviceCaFingerprints.Stored([current, previous, current.ToUpperInvariant()]).ShouldNotBeNull();

        stored.ShouldBe($"{new string('B', 64)},{previous}");
        stored.Length.ShouldBeLessThanOrEqualTo(DeviceCaFingerprints.MaxStoredLength);
        DeviceCaFingerprints.Of(stored).ShouldBe([new string('B', 64), previous]);
        DeviceCaFingerprints.Stored([]).ShouldBeNull("a device that reports none keeps what it last said");
        DeviceCaFingerprints.Stored(null).ShouldBeNull();
        DeviceCaFingerprints.Of(null).ShouldBeEmpty();
    }
}
