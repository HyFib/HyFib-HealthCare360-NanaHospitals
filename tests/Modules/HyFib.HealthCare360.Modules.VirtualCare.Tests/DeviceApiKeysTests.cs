using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class DeviceApiKeysTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 4, 30, 0, TimeSpan.Zero);

    [Fact]
    public void A_key_names_its_device_and_verifies_against_its_hash()
    {
        var deviceId = Guid.CreateVersion7();

        var key = DeviceApiKeys.Create(deviceId);

        key.ShouldStartWith(DeviceApiKeys.Prefix);
        DeviceApiKeys.TryGetDeviceId(key, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(deviceId);
        DeviceApiKeys.Verify(key, DeviceApiKeys.Hash(key)).ShouldBeTrue();
        DeviceApiKeys.Hash(key).ShouldNotContain(key);
    }

    [Fact]
    public void A_tampered_key_fails_verification()
    {
        var key = DeviceApiKeys.Create(Guid.CreateVersion7());
        var hash = DeviceApiKeys.Hash(key);

        DeviceApiKeys.Verify(key[..^1] + (key[^1] == 'a' ? 'b' : 'a'), hash).ShouldBeFalse();
        DeviceApiKeys.Verify(DeviceApiKeys.Create(Guid.CreateVersion7()), hash).ShouldBeFalse();
        DeviceApiKeys.Verify(key, null).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0199b0000000700080000000000000ab.secret")]
    [InlineData("rpm_not-a-guid-at-all-00000000000000.secret")]
    [InlineData("rpm_0199b0000000700080000000000000ab")]
    [InlineData("rpm_0199b0000000700080000000000000ab.")]
    public void Malformed_keys_are_rejected(string? key) => DeviceApiKeys.TryGetDeviceId(key, out _).ShouldBeFalse();

    [Fact]
    public void Overlong_keys_are_rejected() =>
        DeviceApiKeys.TryGetDeviceId(DeviceApiKeys.Create(Guid.CreateVersion7()) + new string('x', 200), out _).ShouldBeFalse();

    [Fact]
    public void Rotating_the_key_retires_the_old_one()
    {
        var device = new RpmDevice { SerialNumber = "OMRON-1", Type = RpmDeviceType.BpMonitor };
        var old = device.IssueApiKey(Now);

        var current = device.IssueApiKey(Now.AddDays(1));

        device.Accepts(old).ShouldBeFalse();
        device.Accepts(current).ShouldBeTrue();
        device.ApiKeyIssuedAt.ShouldBe(Now.AddDays(1));
    }

    [Fact]
    public void A_deactivated_device_accepts_nothing()
    {
        var device = new RpmDevice { SerialNumber = "OMRON-1", Type = RpmDeviceType.BpMonitor };
        var key = device.IssueApiKey(Now);

        device.Deactivate();

        device.Accepts(key).ShouldBeFalse();
        device.EnrollmentId.ShouldBeNull();
    }

    [Fact]
    public void An_assigned_device_goes_silent_once_per_silence()
    {
        var enrollment = new RpmEnrollment { PatientId = Guid.CreateVersion7(), Status = RpmEnrollmentStatus.Active };
        var device = new RpmDevice { SerialNumber = "OMRON-1", Type = RpmDeviceType.BpMonitor };
        device.AssignTo(enrollment, Now);
        var threshold = TimeSpan.FromHours(24);

        device.IsSilent(Now.AddHours(23), threshold).ShouldBeFalse();
        device.IsSilent(Now.AddHours(24), threshold).ShouldBeTrue();

        device.MarkSilenceAlerted(Now.AddHours(24));
        device.IsSilent(Now.AddHours(30), threshold).ShouldBeFalse();

        device.RecordContact(Now.AddHours(31));
        device.IsSilent(Now.AddHours(40), threshold).ShouldBeFalse();
        device.IsSilent(Now.AddHours(56), threshold).ShouldBeTrue();
    }

    [Fact]
    public void Unassigned_devices_are_never_silent()
    {
        var device = new RpmDevice { SerialNumber = "OMRON-1", Type = RpmDeviceType.BpMonitor };

        device.IsSilent(Now.AddDays(10), TimeSpan.FromHours(1)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(RpmDeviceType.BpMonitor, RpmReadingType.SystolicBp, true)]
    [InlineData(RpmDeviceType.BpMonitor, RpmReadingType.HeartRate, true)]
    [InlineData(RpmDeviceType.Thermometer, RpmReadingType.SystolicBp, false)]
    [InlineData(RpmDeviceType.PulseOximeter, RpmReadingType.SpO2, true)]
    [InlineData(RpmDeviceType.EcgPatch, RpmReadingType.EcgRhythm, true)]
    [InlineData(RpmDeviceType.WeightScale, RpmReadingType.Glucose, false)]
    public void Devices_report_only_what_they_can_measure(RpmDeviceType device, RpmReadingType reading, bool expected) =>
        DeviceCapabilities.Measures(device, reading).ShouldBe(expected);
}
