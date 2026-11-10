using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// While the next hospital CA is prepared, System health names the devices that do not hold it yet
/// (<see cref="CaRolloverDevices"/>), from the CAs each reports in its heartbeat (<see cref="DeviceCaFingerprints"/>), and
/// 'certificates switch-ca' waits for them. The names read from the database are tested over the real API (integration test
/// CaRolloverDeviceTests); the wording and when it turns critical here.
/// </summary>
public sealed class CaRolloverDevicesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Nothing_to_say_when_every_device_seen_holds_the_next_CA() =>
        CaRolloverDevices.Warning(new CaRolloverReadiness([], [], 12), Now.AddDays(200), Now).ShouldBeNull();

    [Fact]
    public void Names_the_devices_without_the_next_CA_and_those_not_saying_and_turns_critical_in_the_current_CAs_last_90_days()
    {
        var readiness = new CaRolloverReadiness(["Lab PC 2", "OPD Reception 1"], ["Ward 3 Station"], 12);

        var warning = CaRolloverDevices.Warning(readiness, Now.AddDays(200), Now).ShouldNotBeNull();

        warning.Code.ShouldBe("cert.next_ca_devices");
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldStartWith(
            "The next hospital CA is prepared: 3 of the 12 devices seen in the last 30 days do not hold it yet, so 'certificates switch-ca' waits "
            + $"for them, and the current CA expires on {Now.AddDays(200):dd MMM yyyy}.");
        warning.Message.ShouldContain("Without it: Lab PC 2, OPD Reception 1.");
        warning.Message.ShouldContain("Not saying which CAs they hold (an HC360 Desktop or lab agent older than this server): Ward 3 Station.");
        CaRolloverDevices.Warning(readiness, Now.AddDays(91), Now)!.Severity.ShouldBe("warning");
        CaRolloverDevices.Warning(readiness, Now.AddDays(89), Now)!.Severity.ShouldBe("critical", "as the current CA's own warning, in its last 90 days");
        CaRolloverDevices.Warning(new CaRolloverReadiness([], ["Ward 3 Station"], 1), Now.AddDays(89), Now)!.Severity
            .ShouldBe("critical", "a device that does not say may be cut off as well");
    }

    [Fact]
    public void Names_ten_devices_of_a_group_and_counts_the_others()
    {
        var many = Enumerable.Range(1, 13).Select(i => $"PC {i:00}").ToList();

        CaRolloverDevices.Warning(new CaRolloverReadiness(many, [], 13), Now.AddDays(200), Now)!.Message
            .ShouldContain("Without it: PC 01, PC 02, PC 03, PC 04, PC 05, PC 06, PC 07, PC 08, PC 09, PC 10 and 3 more.");
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
        var next = new string('C', 64);
        var stored = DeviceCaFingerprints.Stored([current, next, current.ToUpperInvariant()]).ShouldNotBeNull();

        stored.ShouldBe($"{new string('B', 64)},{next}");
        stored.Length.ShouldBeLessThanOrEqualTo(DeviceCaFingerprints.MaxStoredLength);
        DeviceCaFingerprints.Of(stored).ShouldBe([new string('B', 64), next]);
        DeviceCaFingerprints.Stored([]).ShouldBeNull("a device that reports none keeps what it last said");
        DeviceCaFingerprints.Stored(null).ShouldBeNull();
        DeviceCaFingerprints.Of(null).ShouldBeEmpty();
    }
}
