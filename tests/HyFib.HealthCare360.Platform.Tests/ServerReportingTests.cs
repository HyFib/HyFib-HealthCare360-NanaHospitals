using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A server of several (SCAL-01) that stops reporting without saying so: a power cut, a crash, its network cable out. One
/// that takes requests is critical, so IT's phone is told, and the warning says whether another server still takes them.
/// One that only stands by for the background jobs is a warning. A server stopped on purpose removes itself instead. When
/// one that was told reports again, its alert says so, with when it was not reporting and when it was again; one that is
/// gone rather than back (stopped on purpose, or removed) is not told back. A warning says since when the server has been
/// silent, and once it has been for a day, how to remove it if it was retired: it is told every day until then.
/// </summary>
public sealed class ServerReportingTests
{
    /// <summary>09:45 in the hospital (India).</summary>
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 4, 15, 0, TimeSpan.Zero);

    private static readonly ServerNodeHealth JobNode = Node("HC360-JOBS", "worker", new JobNodeState(true, Now));

    /// <summary>Before, an API node gone silent was only a warning: IT's phone was not told, and nobody might look.</summary>
    [Fact]
    public void An_api_node_that_stops_reporting_is_critical_and_says_which_servers_take_its_requests()
    {
        var warning = SystemHealthService.ServerWarnings([JobNode, Silent(Node("HC360-API1", "api"), TimeSpan.FromMinutes(12)), Node("HC360-API2", "api")], Now)
            .ShouldHaveSingleItem();

        (warning.Code, warning.Severity, warning.Server).ShouldBe(("server.not_reporting", "critical", "HC360-API1"));
        warning.Message.ShouldBe("HC360-API1 has not reported since 09:33: it may be off, its HC360Server service stopped, "
            + "its network cable out, or the database out of its reach. The reverse proxy sends requests to HC360-API2 meanwhile.");
        SystemHealthAlerts.ToldByTheLinkMonitor(warning.Code).ShouldBeFalse("System health's alerts tell it, so IT's phone gets it");
    }

    [Fact]
    public void The_last_server_taking_requests_that_stops_reporting_says_workstations_cannot_reach_hc360()
    {
        SystemHealthService.ServerWarnings([JobNode, Silent(Node("HC360-API1", "api"), TimeSpan.FromMinutes(12))], Now)
            .ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                w => w.Severity.ShouldBe("critical"),
                w => w.Message.ShouldEndWith("No other server takes requests: workstations cannot reach HC360 until it is back."));
    }

    /// <summary>A whole server standing by for the job lease takes requests too: its silence is critical as well.</summary>
    [Fact]
    public void A_server_standing_by_for_the_jobs_that_takes_requests_is_critical_too()
    {
        SystemHealthService.ServerWarnings([
                Node("HC360-A", "all", new JobNodeState(true, Now)),
                Silent(Node("HC360-B", "all", new JobNodeState(false, null)), TimeSpan.FromMinutes(12))], Now)
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
        var spare = Silent(Node("HC360-SPARE", "worker", new JobNodeState(false, null)), TimeSpan.FromMinutes(12));

        SystemHealthService.ServerWarnings([JobNode, Node("HC360-API1", "api"), spare], Now).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            w => w.Severity.ShouldBe("warning"),
            w => w.Message.ShouldBe("HC360-SPARE, which stands by for the background jobs, has not reported since 09:33: it may be off, "
                + "its HC360Server service stopped, its network cable out, or the database out of its reach. Until it is back, no server takes the jobs "
                + "over if the one that runs them stops."));
    }

    /// <summary>
    /// Its alert lasts until it is back or removed, told every day: a warning that said "for more than three minutes" for
    /// days told nothing of how long. It says since when, with the day once it was not today.
    /// </summary>
    [Fact]
    public void A_silent_server_says_since_when_with_the_day_once_it_is_another_day()
    {
        SystemHealthService.ServerWarnings([JobNode, Silent(Node("HC360-API1", "api"), TimeSpan.FromHours(10))], Now)
            .ShouldHaveSingleItem().Message.ShouldStartWith("HC360-API1 has not reported since 12 Nov 23:45: ");
        SystemHealthService.ServerWarnings([Silent(JobNode, TimeSpan.FromMinutes(12)), Node("HC360-API1", "api")], Now)
            .Single(w => w.Code == "server.not_reporting").Message.ShouldStartWith("HC360-JOBS, which ran the background jobs, has not reported since 09:33: ");
    }

    /// <summary>
    /// After a day, the warning, and the alert told again every day, say how to stop it when the server was retired:
    /// before, the server was dropped after a day, and its alert cleared unseen, whether it was retired or dead.
    /// </summary>
    [Fact]
    public void A_server_silent_for_a_day_says_how_to_remove_it_if_it_was_retired()
    {
        const string hint = " If it was retired, remove it under Administration › System health.";
        SystemHealthService.ServerWarnings([JobNode, Silent(Node("HC360-API1", "api"), TimeSpan.FromDays(1)), Node("HC360-API2", "api")], Now)
            .ShouldHaveSingleItem().Message.ShouldEndWith("The reverse proxy sends requests to HC360-API2 meanwhile." + hint);
        SystemHealthService.ServerWarnings([JobNode, Silent(Node("HC360-API1", "api"), TimeSpan.FromHours(23.9)), Node("HC360-API2", "api")], Now)
            .ShouldHaveSingleItem().Message.ShouldNotContain("retired");
        SystemHealthService.ServerWarnings([Silent(JobNode, TimeSpan.FromDays(3)), Node("HC360-API1", "api")], Now)
            .Single(w => w.Code == "server.not_reporting").Message.ShouldEndWith(hint);
    }

    /// <summary>Before, the alert of a server that reported again cleared without a word: IT drove in for a server that was back.</summary>
    [Fact]
    public void A_server_whose_alert_was_told_is_back_once_it_is_among_the_servers_that_report()
    {
        AlertIncidents.IsBack(Told("HC360-API1"), ["HC360-JOBS", "HC360-API1"]).ShouldBeTrue();
        AlertIncidents.IsBack(Told("HC360-API1"), ["hc360-api1"]).ShouldBeTrue("a Windows machine's name is the same whatever its case");
    }

    /// <summary>
    /// Its warning goes too when the server is stopped on purpose (it removes itself from the servers) or has been silent
    /// for a day (it is dropped from them): a message on its alert's clearing alone told IT a dead server was back.
    /// </summary>
    [Fact]
    public void A_server_gone_rather_than_back_is_not_back()
    {
        AlertIncidents.IsBack(Told("HC360-API1"), ["HC360-JOBS", "HC360-API2"]).ShouldBeFalse();
        AlertIncidents.IsBack(Told("HC360-API1"), []).ShouldBeFalse();
        AlertIncidents.IsBack(Told("HC360-API1"), null).ShouldBeFalse("a single Server Node reads no other server's report");
    }

    [Fact]
    public void Only_the_alert_of_a_server_not_reporting_that_was_told_is_told_back()
    {
        var neverTold = Told("HC360-API1");
        neverTold.ToldAt = null;
        AlertIncidents.IsBack(neverTold, ["HC360-API1"]).ShouldBeFalse("nobody was told it was not reporting");

        var certificate = Told("HC360-API1");
        certificate.Code = "certificate.expiring";
        AlertIncidents.IsBack(certificate, ["HC360-API1"]).ShouldBeFalse("a server that reports may still have the warning that cleared");
    }

    /// <summary>
    /// The return says when it was not reporting (its alert went) and when it was again, in the hospital's time: it stands
    /// alone when the alert of its silence was withdrawn before it went, and says how long it lasted.
    /// </summary>
    [Fact]
    public void The_return_says_when_it_was_not_reporting_and_when_it_was_again_in_hospital_time()
    {
        AlertIncidents.BackMessage("HC360-API1", new DateTimeOffset(2026, 11, 13, 4, 3, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 13, 4, 15, 0, TimeSpan.Zero))
            .ShouldBe("HC360-API1 is reporting again: it is on, its HC360Server service runs and it reaches the database. "
                + "Not reporting at 09:33, reporting again at 09:45.");
    }

    [Fact]
    public void A_return_on_another_day_names_both_days()
    {
        AlertIncidents.BackMessage("HC360-API1", new DateTimeOffset(2026, 11, 13, 18, 20, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 13, 18, 40, 0, TimeSpan.Zero))
            .ShouldEndWith("Not reporting at 13 Nov 23:50, reporting again at 14 Nov 00:10.");
    }

    /// <summary>The alert raised and told for <paramref name="server"/> not reporting.</summary>
    private static AlertIncident Told(string server) => new()
    {
        Code = SystemHealthService.NotReportingCode,
        Server = server,
        Message = $"{server} has not reported since 09:33.",
        RaisedAt = DateTimeOffset.UtcNow,
        ToldAt = DateTimeOffset.UtcNow,
    };

    /// <summary><paramref name="node"/> as when it last reported <paramref name="ago"/> before <see cref="Now"/>.</summary>
    private static ServerNodeHealth Silent(ServerNodeHealth node, TimeSpan ago) => node with { Reporting = false, ReportedAt = Now - ago };

    private static ServerNodeHealth Node(string name, string role, JobNodeState? jobs = null) => new(
        new ServerInfo(name, "Windows", "1.0", "Production", role, Now.AddDays(-3), 1, 4, 1, ".NET", Node: name, Jobs: jobs),
        new NetworkInfo(false, null, null, null, [], [], 443, 7361, false),
        new StorageInfo("D:", 100, 100),
        new CertificateInfo(null, null, []),
        new PowerInfo(false, null, null, null),
        [],
        Now);
}
