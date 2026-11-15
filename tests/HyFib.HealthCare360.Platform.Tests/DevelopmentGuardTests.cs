using HyFib.HealthCare360.Platform.Hosting;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A server installed as the Windows service never runs as Development (<see cref="DevelopmentGuard"/>), and the developer
/// settings file is kept out of every publish.
/// </summary>
public sealed class DevelopmentGuardTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    [InlineData("DEVELOPMENT")]
    public void The_service_refuses_to_start_as_Development_and_says_what_to_remove(string environment)
    {
        var problem = DevelopmentGuard.Problem(environment, runsAsWindowsService: true).ShouldNotBeNull();

        problem.ShouldContain("ASPNETCORE_ENVIRONMENT");
        problem.ShouldContain("backups and MFA");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    [InlineData(null)]
    public void The_service_starts_in_any_other_environment(string? environment) =>
        DevelopmentGuard.Problem(environment, runsAsWindowsService: true).ShouldBeNull();

    [Fact]
    public void A_developers_console_run_may_be_Development() =>
        DevelopmentGuard.Problem("Development", runsAsWindowsService: false).ShouldBeNull();

    [Fact]
    public void The_developer_settings_file_is_never_copied_to_a_publish_folder()
    {
        var project = ReleaseWorkflowTests.Read("src", "server", "HyFib.HealthCare360.Server", "HyFib.HealthCare360.Server.csproj");

        project.ShouldContain("<Content Update=\"appsettings.Development.json\" CopyToPublishDirectory=\"Never\" />");
    }

    [Fact]
    public void The_release_script_stops_if_the_developer_settings_reached_the_server_package()
    {
        var script = ReleaseWorkflowTests.Read("deploy", "windows", "Publish-HC360.ps1");

        var check = script.IndexOf("appsettings.Development.json", StringComparison.Ordinal);
        check.ShouldBeGreaterThan(0);
        // Checked before the folder is zipped.
        check.ShouldBeLessThan(script.IndexOf("Compress-Archive", StringComparison.Ordinal));
    }
}
