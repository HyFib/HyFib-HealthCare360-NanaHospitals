using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class EmployeeDirectoryTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateOnly Joined = new(2024, 4, 1);

    [Theory]
    [InlineData(" nhemp00042 ", "NHEMP00042")]
    [InlineData("NHEMP00042", "NHEMP00042")]
    public void Codes_are_trimmed_and_upper_cased(string input, string expected) =>
        EmployeeDirectory.NormalizeCode(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NH-EMP-42")]
    [InlineData("NHEMP00042' OR 1=1")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXY")]
    public void Anything_that_cannot_be_a_code_is_rejected_before_the_database(string? input) =>
        EmployeeDirectory.NormalizeCode(input).ShouldBeNull();

    [Theory]
    [InlineData(EmployeeStatus.Active, true)]
    [InlineData(EmployeeStatus.Probation, true)]
    [InlineData(EmployeeStatus.OnNotice, true)]
    [InlineData(EmployeeStatus.Suspended, false)]
    [InlineData(EmployeeStatus.Separated, false)]
    public void Only_working_statuses_are_active(EmployeeStatus status, bool expected) =>
        EmployeeDirectory.IsActiveOn(status, Joined, null, Today).ShouldBe(expected);

    [Fact]
    public void An_employee_who_has_not_joined_yet_is_not_active() =>
        EmployeeDirectory.IsActiveOn(EmployeeStatus.Active, Today.AddDays(1), null, Today).ShouldBeFalse();

    [Fact]
    public void Serving_notice_is_active_up_to_and_including_the_last_working_day()
    {
        EmployeeDirectory.IsActiveOn(EmployeeStatus.OnNotice, Joined, Today, Today).ShouldBeTrue();
        EmployeeDirectory.IsActiveOn(EmployeeStatus.OnNotice, Joined, Today.AddDays(-1), Today).ShouldBeFalse();
    }

    [Fact]
    public void The_module_registers_the_directory_for_other_modules()
    {
        var services = new ServiceCollection();

        new HRModule().AddServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        services.ShouldContain(s => s.ServiceType == typeof(IEmployeeDirectory) && s.ImplementationType == typeof(EmployeeDirectory)
                                    && s.Lifetime == ServiceLifetime.Scoped);
    }
}
