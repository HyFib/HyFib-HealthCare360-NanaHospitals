using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A server of several (SCAL-01) that stops reporting without saying so: a power cut, a crash, its network cable out. One
/// that takes requests is critical, so IT's phone is told, and the warning says whether another server still takes them.
/// One that only stands by for the background jobs is a warning. A server stopped on purpose removes itself instead.
/// </summary>
public sealed class ServerReportingTests
{
    private static readonly ServerNodeHealth JobNode = Node("HC360-JOBS", "worker", new JobNodeState(true, DateTimeOffset.UtcNow));

    /// <summary>Before, an API node gone silent was only a warning: IT's phone was not told, and nobody might look.</summary>
    [Fact]
    public void An_api_node_that_stops_reporting_is_critical_and_says_which_servers_take_its_requests()
    {
        var warning = SystemHealthService.ServerWarnings([JobNode, Node("HC360-API1", "api") with { Reporting = false }, Node("HC360-API2", "api")])
            .ShouldHaveSingleItem();

        (warning.Code, warning.Severity, warning.Server).ShouldBe(("server.not_reporting", "critical", "HC360-API1"));
        warning.Message.ShouldBe("HC360-API1 has not reported for more than three minutes: it may be off, its HC360Server service stopped, "
            + "its network cable out, or the database out of its reach. The reverse proxy sends requests to HC360-API2 meanwhile.");
        SystemHealthAlerts.ToldByTheLinkMonitor(warning.Code).ShouldBeFalse("System health's alerts tell it, so IT's phone gets it");
    }

    [Fact]
    public void The_last_server_taking_requests_that_stops_reporting_says_workstations_cannot_reach_hc360()
    {
        SystemHealthService.ServerWarnings([JobNode, Node("HC360-API1", "api") with { Reporting = false }])
            .ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                w => w.Severity.ShouldBe("critical"),
                w => w.Message.ShouldEndWith("No other server takes requests: workstations cannot reach HC360 until it is back."));
    }

    /// <summary>A whole server standing by for the job lease takes requests too: its silence is critical as well.</summary>
    [Fact]
    public void A_server_standing_by_for_the_jobs_that_takes_requests_is_critical_too()
    {
        SystemHealthService.ServerWarnings([
                Node("HC360-A", "all", new JobNodeState(true, DateTimeOffset.UtcNow)),
                Node("HC360-B", "all", new JobNodeState(false, null)) with { Reporting = false }])
            .Single(w => w.Code == "server.not_reporting").ShouldSatisfyAllConditions(
                w => w.Severity.ShouldBe("critical"),
                w => w.Message.ShouldEndWith("The reverse proxy sends requests to HC360-A meanwhile."));
    }

    /// <summary>
    /// A worker that only stands by for the jobs takes no requests: nothing stops until the job node does too, so it is a
    /// warning, and it does not say the reverse proxy sends its requests elsewhere, as it said before.
    /// </summary>
    [Fact]
    public void A_worker_standing_by_that_stops_reporting_is_a_warning()
    {
        var spare = Node("HC360-SPARE", "worker", new JobNodeState(false, null)) with { Reporting = false };

        SystemHealthService.ServerWarnings([JobNode, Node("HC360-API1", "api"), spare]).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            w => w.Severity.ShouldBe("warning"),
            w => w.Message.ShouldBe("HC360-SPARE, which stands by for the background jobs, has not reported for more than three minutes: it may be off, "
                + "its HC360Server service stopped, its network cable out, or the database out of its reach. Until it is back, no server takes the jobs "
                + "over if the one that runs them stops."));
    }

    private static ServerNodeHealth Node(string name, string role, JobNodeState? jobs = null) => new(
        new ServerInfo(name, "Windows", "1.0", "Production", role, DateTimeOffset.UtcNow, 1, 4, 1, ".NET", Node: name, Jobs: jobs),
        new NetworkInfo(false, null, null, null, [], [], 443, 7361, false),
        new StorageInfo("D:", 100, 100),
        new CertificateInfo(null, null, []),
        new PowerInfo(false, null, null, null),
        [],
        DateTimeOffset.UtcNow);
}
