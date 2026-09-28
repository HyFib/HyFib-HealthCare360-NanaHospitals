using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class RpmDeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 4, 30, 0, TimeSpan.Zero);
    private static readonly Guid Facility = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");
    private static readonly Guid OtherFacility = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000b");

    [Fact]
    public void A_retired_device_comes_back_unassigned_and_needs_a_new_key()
    {
        var device = new RpmDevice { FacilityId = Facility, SerialNumber = "BP-001", Type = RpmDeviceType.BpMonitor };
        var oldKey = device.IssueApiKey(Now);
        device.RecordContact(Now);
        device.Deactivate();

        device.Reactivate(OtherFacility, RpmDeviceType.Glucometer, "Accu-Chek", "Guide");

        device.IsActive.ShouldBeTrue();
        device.FacilityId.ShouldBe(OtherFacility);
        device.Type.ShouldBe(RpmDeviceType.Glucometer);
        device.EnrollmentId.ShouldBeNull();
        device.PatientId.ShouldBeNull();
        device.LastSeenAt.ShouldBeNull();
        device.Accepts(oldKey).ShouldBeFalse();

        var newKey = device.IssueApiKey(Now);
        device.Accepts(newKey).ShouldBeTrue();
    }

    [Fact]
    public void A_retired_device_accepts_no_key()
    {
        var device = new RpmDevice { FacilityId = Facility, SerialNumber = "SPO2-7", Type = RpmDeviceType.PulseOximeter };
        var key = device.IssueApiKey(Now);

        device.Deactivate();

        device.Accepts(key).ShouldBeFalse();
    }
}
