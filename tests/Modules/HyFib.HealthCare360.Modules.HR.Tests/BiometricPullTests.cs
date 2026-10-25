using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class BiometricPullTests
{
    [Theory]
    [InlineData("192.168.1.201", true)]
    [InlineData(" 10.0.5.21 ", true)]
    [InlineData("172.16.0.9", true)]
    [InlineData("fd12:3456::21", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("fe80::1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("terminal.local", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Terminals_are_read_only_at_an_IP_address_on_a_network(string? address, bool allowed) =>
        BiometricDevice.IsAllowedPullAddress(address).ShouldBe(allowed);

    [Fact]
    public void A_terminal_read_rarely_is_quiet_only_after_two_missed_reads()
    {
        BiometricPullService.QuietWindow(new BiometricDevice()).ShouldBe(TimeSpan.FromMinutes(30));
        BiometricPullService.QuietWindow(new BiometricDevice { Connection = BiometricConnection.Pull, PullEveryMinutes = 10 }).ShouldBe(TimeSpan.FromMinutes(30));
        BiometricPullService.QuietWindow(new BiometricDevice { Connection = BiometricConnection.Pull, PullEveryMinutes = 60 }).ShouldBe(TimeSpan.FromMinutes(125));
    }

    [Fact]
    public void A_terminal_HC360_reads_needs_its_address_and_a_sensible_port_and_interval()
    {
        var validator = new SaveBiometricDeviceRequestValidator();
        SaveBiometricDeviceRequest Request(string? address, int? port = null, int? minutes = null) =>
            new("CQZ7232460163", "Main gate", null, null, null, null, true, null, BiometricConnection.Pull, address, port, minutes);

        validator.Validate(Request("192.168.1.201")).IsValid.ShouldBeTrue();
        validator.Validate(Request("192.168.1.201", 4370, 15)).IsValid.ShouldBeTrue();
        validator.Validate(Request(null)).Errors.ShouldContain(e => e.PropertyName == "PullAddress");
        validator.Validate(Request("127.0.0.1")).Errors.ShouldContain(e => e.PropertyName == "PullAddress");
        validator.Validate(Request("192.168.1.201", 70_000)).Errors.ShouldContain(e => e.PropertyName == "PullPort");
        validator.Validate(Request("192.168.1.201", null, 0)).Errors.ShouldContain(e => e.PropertyName == "PullEveryMinutes");
        validator.Validate(Request("192.168.1.201", null, BiometricDevice.MaxPullMinutes + 1)).IsValid.ShouldBeFalse();

        // A terminal that pushes needs none of it, only the address it pushes from (R-5).
        validator.Validate(Request(null) with { Connection = BiometricConnection.Push, AllowedIpAddress = "192.168.1.201" }).IsValid.ShouldBeTrue();
        validator.Validate(Request(null) with { Connection = BiometricConnection.Push }).Errors.ShouldContain(e => e.PropertyName == "AllowedIpAddress");
    }

    [Fact]
    public void A_training_punch_needs_an_employee_number_the_terminal_could_hold()
    {
        var validator = new TrainingPunchRequestValidator();

        validator.Validate(new TrainingPunchRequest("1001")).IsValid.ShouldBeTrue();
        validator.Validate(new TrainingPunchRequest("")).IsValid.ShouldBeFalse();
        validator.Validate(new TrainingPunchRequest("10 01")).IsValid.ShouldBeFalse();
        validator.Validate(new TrainingPunchRequest(new string('9', 25))).IsValid.ShouldBeFalse();
    }
}
