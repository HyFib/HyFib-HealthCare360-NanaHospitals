using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Server;
using Microsoft.Extensions.Configuration;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SCAL-05: <c>--role all|api|worker</c> on the server's command line sets the process role, so a worker node runs the
/// background jobs and serves no API.
/// </summary>
public sealed class ServerArgumentsTests
{
    [Theory]
    [InlineData(new[] { "--role", "worker" }, "worker")]
    [InlineData(new[] { "--role=api" }, "api")]
    [InlineData(new[] { "--ROLE", "All" }, "all")]
    [InlineData(new[] { "--urls", "https://+:7360", "--role", "Worker" }, "worker")]
    public void The_role_switch_sets_the_process_role(string[] args, string role)
    {
        var options = Options(ServerArguments.Map(args));

        options.Role.ShouldBe(role);
    }

    [Fact]
    public void A_worker_runs_jobs_and_serves_no_api()
    {
        var options = Options(ServerArguments.Map(["--role", "worker"]));

        options.RunsJobs.ShouldBeTrue();
        options.ServesApi.ShouldBeFalse();
    }

    [Fact]
    public void Other_arguments_are_passed_on_as_they_are() =>
        ServerArguments.Map(["--urls", "https://+:7360", "--environment", "Production"])
            .ShouldBe(["--urls", "https://+:7360", "--environment", "Production"]);

    [Fact]
    public void Without_the_switch_the_role_stays_all() =>
        Options(ServerArguments.Map([])).Role.ShouldBe("all");

    [Theory]
    [InlineData("--role gateway")]
    [InlineData("--role")]
    [InlineData("--role=")]
    public void An_unknown_or_missing_role_stops_start_up(string commandLine) =>
        Should.Throw<ArgumentException>(() => ServerArguments.Map(commandLine.Split(' ')))
            .Message.ShouldStartWith("--role must be one of all, api, worker");

    private static PlatformOptions Options(string[] args) =>
        new ConfigurationBuilder().AddCommandLine(args).Build().GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
}
