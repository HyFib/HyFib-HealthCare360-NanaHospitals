using System.Net;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// System health with several servers (SCAL-01): an API node answers it, a different one from one request to the next
/// behind the reverse proxy, and the node that runs the jobs (the backups, the outbox, the listeners) answers no requests.
/// Each server writes what it reports of its own machine to the database, and System health shows every one, with when
/// each last reported. A single Server Node shows itself alone, as it always did.
/// </summary>
public sealed class SystemHealthServersTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task System_health_on_an_API_node_shows_every_server_the_job_node_among_them()
    {
        var jobs = new ServerNodeBoard(server.Services.GetRequiredService<IServiceScopeFactory>(), NodeFolder());
        var machine = "IT-JOBS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        try
        {
            // The node that runs the jobs reports itself: its data drive is nearly full.
            await jobs.PublishAsync(JobNode(machine), Ct);

            var report = await ApiNodeReportAsync();
            var servers = report.Servers.ShouldNotBeNull("System health on an API node of several should show every server");
            servers.Count.ShouldBe(2);
            servers[0].Server.MachineName.ShouldBe(machine, "the node that runs the jobs comes first");
            servers[0].Server.Role.ShouldBe("worker");
            servers[0].Reporting.ShouldBeTrue();
            (DateTimeOffset.UtcNow - servers[0].ReportedAt).Duration().ShouldBeLessThan(TimeSpan.FromMinutes(1));
            servers[1].Server.MachineName.ShouldBe(Environment.MachineName);
            servers[1].Server.Role.ShouldBe("api");
            report.Warnings.ShouldContain(w => w.Code == "disk.low" && w.Server == machine, "the warnings name the server they are about");
            report.Warnings.ShouldNotContain(w => w.Code.StartsWith("server.", StringComparison.Ordinal));

            // A single Server Node shows only itself, whatever the database holds.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var single = await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct);
                single.Servers.ShouldBeNull();
                single.Warnings.ShouldNotContain(w => w.Server != null || w.Code.StartsWith("server.", StringComparison.Ordinal));
            }

            // It stops reporting without saying so, as in a power cut.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
                    $"UPDATE platform.server_nodes SET reported_at = now() - interval '10 minutes' WHERE node = {jobs.Node}", Ct);
            }

            var silent = await ApiNodeReportAsync();
            var stopped = silent.Servers.ShouldNotBeNull().Single(s => s.Server.MachineName == machine);
            stopped.Reporting.ShouldBeFalse();
            var notReporting = silent.Warnings.Single(w => w.Code == "server.not_reporting");
            (notReporting.Severity, notReporting.Server).ShouldBe(("critical", machine));
            silent.Warnings.ShouldNotContain(w => w.Code == "disk.low" && w.Server == machine, "what a server said before it stopped reporting is not shown as current");

            // Stopped and gone: no server runs the jobs.
            await jobs.WithdrawAsync(Ct);
            var none = await ApiNodeReportAsync();
            none.Servers.ShouldNotBeNull().ShouldHaveSingleItem().Server.Role.ShouldBe("api");
            none.Warnings.Single(w => w.Code == "server.no_jobs").Severity.ShouldBe("critical");
        }
        finally
        {
            await jobs.WithdrawAsync(Ct);
        }
    }

    /// <summary>
    /// A server that stops without saying so (a power cut, a crash) and starts again writes over its own row: System health
    /// shows it once, and reporting. Each run of a server had a row of its own, so for a day the job node showed twice,
    /// once as not reporting, with a critical warning that nothing made the backups while it made them.
    /// </summary>
    [Fact]
    public async Task A_server_that_starts_again_after_a_power_cut_is_shown_once()
    {
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var folder = NodeFolder();
        var machine = "IT-JOBS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var before = new ServerNodeBoard(scopes, folder);
        var after = new ServerNodeBoard(scopes, folder);
        try
        {
            await before.PublishAsync(JobNode(machine), Ct);

            // Its power is cut: its row stays, and goes silent.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
                    $"UPDATE platform.server_nodes SET reported_at = now() - interval '10 minutes' WHERE node = {before.Node}", Ct);
            }

            // It starts again, with its own data folder, and reports.
            await after.PublishAsync(JobNode(machine), Ct);

            var report = await ApiNodeReportAsync();
            var shown = report.Servers.ShouldNotBeNull().Where(s => s.Server.MachineName == machine).ToList();
            shown.Count.ShouldBe(1, "a server that starts again should be shown once");
            shown[0].Reporting.ShouldBeTrue();
            report.Warnings.ShouldNotContain(w => w.Code.StartsWith("server.", StringComparison.Ordinal));
        }
        finally
        {
            await after.WithdrawAsync(Ct);
            await before.WithdrawAsync(Ct);
        }
    }

    /// <summary>
    /// The alerts, from System health with several servers (DED-17, SCAL-01): an API node that stops reporting is told, and
    /// once it reports again it is told back, judged from the same report. One stopped on purpose removes its row: its
    /// alert clears too, but it is not back, and nobody is told it is.
    /// </summary>
    [Fact]
    public async Task An_api_node_that_reports_again_is_told_back_and_one_stopped_on_purpose_is_not()
    {
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var jobs = new ServerNodeBoard(scopes, NodeFolder());
        var back = new ServerNodeBoard(scopes, NodeFolder());
        var stopped = new ServerNodeBoard(scopes, NodeFolder());
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (backName, stoppedName) = ("IT-API-" + suffix, "IT-OFF-" + suffix);
        try
        {
            await jobs.PublishAsync(JobNode("IT-JOBS-" + suffix) with { Warnings = [] }, Ct);
            await back.PublishAsync(ApiNode(backName), Ct);
            await stopped.PublishAsync(ApiNode(stoppedName), Ct);
            await SilenceAsync(back.Node);
            await SilenceAsync(stopped.Node);
            var toldAt = DateTimeOffset.UtcNow;
            await AlertsAsync(await ApiNodeReportAsync(), toldAt);
            (await NotReportingAsync(backName)).ToldAt.ShouldNotBeNull("an API node gone silent is critical, and told");
            (await NotReportingAsync(stoppedName)).ToldAt.ShouldNotBeNull();

            // One starts again and reports; the other is stopped on purpose, which removes its row.
            await back.PublishAsync(ApiNode(backName), Ct);
            await stopped.WithdrawAsync(Ct);
            await AlertsAsync(await ApiNodeReportAsync(), toldAt.AddMinutes(10));

            (await NotReportingAsync(backName)).ClearedAt.ShouldNotBeNull();
            (await NotReportingAsync(stoppedName)).ClearedAt.ShouldNotBeNull();
            (await ToldBackAsync(backName)).ShouldBeGreaterThan(0, "everyone who may see System health is told it is back");
            (await ToldBackAsync(stoppedName)).ShouldBe(0, "a server stopped on purpose is not back");
        }
        finally
        {
            await stopped.WithdrawAsync(Ct);
            await back.WithdrawAsync(Ct);
            await jobs.WithdrawAsync(Ct);

            // The alerts as this single server runs them: those of the servers above clear without a word.
            await using var scope = server.Services.CreateAsyncScope();
            await AlertsAsync(await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct), DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// A server that stopped without saying so and stays silent for days, as one whose power supply died: it stays on System
    /// health, critically, saying since when and how to remove it if it was retired, and its alert is told again every day.
    /// Before, its row was dropped after a day, and the alert cleared unseen, a few minutes before its reminder was due.
    /// </summary>
    [Fact]
    public async Task A_server_silent_for_days_stays_and_its_alert_is_told_again_every_day()
    {
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var jobs = new ServerNodeBoard(scopes, NodeFolder());
        var dead = new ServerNodeBoard(scopes, NodeFolder());
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var name = "IT-DEAD-" + suffix;
        try
        {
            await jobs.PublishAsync(JobNode("IT-JOBS-" + suffix) with { Warnings = [] }, Ct);
            await dead.PublishAsync(ApiNode(name), Ct);
            await SilenceAsync(dead.Node, "2 days");
            await jobs.PublishAsync(JobNode("IT-JOBS-" + suffix) with { Warnings = [] }, Ct);

            var report = await ApiNodeReportAsync();
            report.Servers.ShouldNotBeNull().Single(s => s.Server.MachineName == name).Reporting.ShouldBeFalse();
            report.Warnings.Single(w => w.Code == SystemHealthService.NotReportingCode && w.Server == name).ShouldSatisfyAllConditions(
                w => w.Severity.ShouldBe("critical"),
                w => w.Message.ShouldStartWith($"{name} has not reported since "),
                w => w.Message.ShouldEndWith(" If it was retired, remove it under Administration › System health."));

            var toldAt = DateTimeOffset.UtcNow;
            await AlertsAsync(report, toldAt);
            await AlertsAsync(await ApiNodeReportAsync(), toldAt.Add(SystemHealthAlerts.RemindAfter).AddMinutes(1));
            var alert = await NotReportingAsync(name);
            (alert.ClearedAt, alert.RemindedAt is not null).ShouldBe((null, true), "still open, and told again a day later");
        }
        finally
        {
            await dead.WithdrawAsync(Ct);
            await jobs.WithdrawAsync(Ct);
            await using var scope = server.Services.CreateAsyncScope();
            await AlertsAsync(await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct), DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// A server set up again in the same machine and data folder, its id made anew (the folder made again), takes its old
    /// row's place: the old one would otherwise stay, not reporting, now that a silent server's row is kept. A second server
    /// on the same machine, in a data folder of its own, keeps its own.
    /// </summary>
    [Fact]
    public async Task A_server_set_up_again_in_the_same_place_replaces_its_old_row()
    {
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var before = new ServerNodeBoard(scopes, NodeFolder());
        var after = new ServerNodeBoard(scopes, NodeFolder());
        var second = new ServerNodeBoard(scopes, NodeFolder());
        var name = "IT-JOBS-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        try
        {
            await before.PublishAsync(JobNode(name) with { Warnings = [] }, Ct);
            await SilenceAsync(before.Node, "10 minutes");
            after.Node.ShouldNotBe(before.Node);
            await after.PublishAsync(JobNode(name) with { Warnings = [] }, Ct);

            (await ReportOfAsync(before.Node)).ShouldBeNull("the server set up again removed its old row");
            var shown = (await ApiNodeReportAsync()).Servers.ShouldNotBeNull().Where(s => s.Server.MachineName == name).ToList();
            shown.ShouldHaveSingleItem().Reporting.ShouldBeTrue();

            var elsewhere = JobNode(name) with { Warnings = [] };
            await second.PublishAsync(elsewhere with { Storage = elsewhere.Storage with { DataDirectory = @"D:\HC360-second" } }, Ct);
            (await ReportOfAsync(after.Node)).ShouldNotBeNull("another server on the same machine, in a folder of its own, is not the same one");
        }
        finally
        {
            await second.WithdrawAsync(Ct);
            await after.WithdrawAsync(Ct);
            await before.WithdrawAsync(Ct);
        }
    }

    /// <summary>
    /// A silent server removed by whoever manages the server, once it was retired, renamed or set up elsewhere: its alert is
    /// acknowledged by them, so nobody further is paged, and it clears without telling anyone the server is back. One that
    /// reports cannot be removed, and those who only see System health cannot remove one. The removal is audited.
    /// </summary>
    [Fact]
    public async Task A_silent_server_is_removed_by_whoever_manages_the_server_and_its_alert_acknowledged()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var jobs = new ServerNodeBoard(scopes, NodeFolder());
        var retired = new ServerNodeBoard(scopes, NodeFolder());
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var name = "IT-OLD-" + suffix;
        try
        {
            await jobs.PublishAsync(JobNode("IT-JOBS-" + suffix) with { Warnings = [] }, Ct);
            await retired.PublishAsync(ApiNode(name), Ct);
            await SilenceAsync(retired.Node, "2 days");
            var toldAt = DateTimeOffset.UtcNow;
            await AlertsAsync(await ApiNodeReportAsync(), toldAt);
            (await NotReportingAsync(name)).ToldAt.ShouldNotBeNull();

            using (var refused = await hospitalAdmin.SendRawAsync(HttpMethod.Delete, $"{Servers}/{retired.Node}"))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "only those who manage the server remove one");
            }

            using (var reporting = await admin.SendRawAsync(HttpMethod.Delete, $"{Servers}/{jobs.Node}"))
            {
                reporting.StatusCode.ShouldBe(HttpStatusCode.Conflict, "a server that reports removes itself when it is stopped");
            }

            using (var removed = await admin.SendRawAsync(HttpMethod.Delete, $"{Servers}/{retired.Node}"))
            {
                removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            (await ReportOfAsync(retired.Node)).ShouldBeNull();
            (await NotReportingAsync(name)).AcknowledgedBy.ShouldNotBeNullOrWhiteSpace("whoever removes it is on it: nobody further is paged");
            (await RemovalsAsync(name)).ShouldBe(1);
            using (var again = await admin.SendRawAsync(HttpMethod.Delete, $"{Servers}/{retired.Node}"))
            {
                again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            // Its warning is gone, and so is its alert: cleared, but not told back.
            var after = await ApiNodeReportAsync();
            after.Servers.ShouldNotBeNull().ShouldNotContain(s => s.Server.MachineName == name);
            await AlertsAsync(after, toldAt.AddMinutes(10));
            (await NotReportingAsync(name)).ClearedAt.ShouldNotBeNull();
            (await ToldBackAsync(name)).ShouldBe(0, "a server removed is gone, not back");
        }
        finally
        {
            await retired.WithdrawAsync(Ct);
            await jobs.WithdrawAsync(Ct);
            await using var scope = server.Services.CreateAsyncScope();
            await AlertsAsync(await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildAsync(Ct), DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Exactly one server should run the background jobs: each that does makes the nightly backup and listens for analysers
    /// and modalities. An API node whose settings miss "Role": "api" runs them as "all", and nothing said so. One that no
    /// longer reports runs nothing.
    /// </summary>
    [Fact]
    public async Task More_than_one_server_that_runs_the_jobs_is_warned_about()
    {
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var jobs = new ServerNodeBoard(scopes, NodeFolder());
        var api = new ServerNodeBoard(scopes, NodeFolder());
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        try
        {
            await jobs.PublishAsync(JobNode("IT-JOBS-" + suffix), Ct);
            await api.PublishAsync(JobNode("IT-API-" + suffix, role: "all"), Ct);

            var report = await ApiNodeReportAsync();
            var several = report.Warnings.Single(w => w.Code == "server.several_jobs");
            (several.Severity, several.Server).ShouldBe(("warning", (string?)null), "it is about no one server");
            several.Message.ShouldStartWith($"IT-API-{suffix} and IT-JOBS-{suffix} both run the background jobs, and only one server should");

            // The API node stops reporting: it runs nothing meanwhile.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
                    $"UPDATE platform.server_nodes SET reported_at = now() - interval '10 minutes' WHERE node = {api.Node}", Ct);
            }

            (await ApiNodeReportAsync()).Warnings.ShouldNotContain(w => w.Code == "server.several_jobs");
        }
        finally
        {
            await jobs.WithdrawAsync(Ct);
            await api.WithdrawAsync(Ct);
        }
    }

    /// <summary>
    /// With the job lease (SCAL-05), System health shows the server that holds it as the one running the jobs, read from the
    /// database's lock at once rather than from what each server wrote up to a minute ago, and the others as standing by.
    /// Between one holder and the next it says another is taking over, not that nothing runs the jobs.
    /// </summary>
    [Fact]
    public async Task With_the_job_lease_System_health_shows_who_holds_it_now()
    {
        var scopes = server.Services.GetRequiredService<IServiceScopeFactory>();
        var first = new ServerNodeBoard(scopes, NodeFolder());
        var second = new ServerNodeBoard(scopes, NodeFolder());
        var suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        try
        {
            // Both wrote a minute ago that they stand by; the first has taken the lease since.
            await first.PublishAsync(LeaseNode("IT-A-" + suffix, first.Node), Ct);
            await second.PublishAsync(LeaseNode("IT-B-" + suffix, second.Node), Ct);
            var connection = new NpgsqlConnectionStringBuilder(server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital"))
            {
                ApplicationName = "HC360 jobs " + first.Node,
                Pooling = false,
            };
            await using (var holder = new NpgsqlConnection(connection.ConnectionString))
            {
                await holder.OpenAsync(Ct);
                await using (var take = new NpgsqlCommand($"SELECT pg_advisory_lock({JobLeadership.LockNamespace}, {JobLeadership.LockKey})", holder))
                {
                    await take.ExecuteNonQueryAsync(Ct);
                }

                var report = await ApiNodeReportAsync();
                var servers = report.Servers.ShouldNotBeNull();
                servers[0].Server.MachineName.ShouldBe("IT-A-" + suffix, "the server holding the lease comes first");
                servers[0].Server.Jobs.ShouldNotBeNull().Active.ShouldBeTrue();
                servers.Single(s => s.Server.MachineName == "IT-B-" + suffix).Server.Jobs.ShouldNotBeNull().Active.ShouldBeFalse();
                report.Warnings.ShouldNotContain(w => w.Code.StartsWith("server.", StringComparison.Ordinal));
            }

            // The holder's connection has ended: the lease is free until the other takes it.
            var between = await ApiNodeReportAsync();
            between.Servers.ShouldNotBeNull().ShouldNotContain(s => s.Server.Jobs != null && s.Server.Jobs.Active);
            between.Warnings.Single(w => w.Code.StartsWith("server.", StringComparison.Ordinal)).ShouldSatisfyAllConditions(
                w => w.Code.ShouldBe("server.jobs_taking_over"),
                w => w.Severity.ShouldBe("warning"));
        }
        finally
        {
            await first.WithdrawAsync(Ct);
            await second.WithdrawAsync(Ct);
        }
    }

    /// <summary>The part a server that takes turns at the jobs writes: standing by, as it last said.</summary>
    private static ServerNodeHealth LeaseNode(string machine, string node)
    {
        var part = JobNode(machine, role: "all");
        return part with { Server = part.Server with { Node = node, Jobs = new JobNodeState(false, null) }, Warnings = [] };
    }

    /// <summary>A node of several writes its part as it starts and removes it as it stops.</summary>
    [Fact]
    public async Task A_node_of_several_shares_its_part_while_it_runs()
    {
        var board = new ServerNodeBoard(server.Services.GetRequiredService<IServiceScopeFactory>(), NodeFolder());
        var sharing = ActivatorUtilities.CreateInstance<ServerNodeSharing>(server.Services, board, SharedDirectories());
        await sharing.StartAsync(Ct);
        try
        {
            (await SecondNode.EventuallyAsync(async () => await ReportOfAsync(board.Node) is not null, TimeSpan.FromSeconds(30), Ct))
                .ShouldBeTrue("a node of several should share its part as it starts");
            (await ReportOfAsync(board.Node)).ShouldNotBeNull().ShouldContain(Environment.MachineName);
        }
        finally
        {
            await sharing.StopAsync(Ct);
        }

        (await ReportOfAsync(board.Node)).ShouldBeNull("a node removes its part as it stops");
    }

    /// <summary>
    /// System health as an API node of several builds it: the test server's services, with a shared folder and the role
    /// <c>api</c>.
    /// </summary>
    private async Task<SystemHealthReport> ApiNodeReportAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var options = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        var api = new PlatformOptions
        {
            Role = "api",
            DataDirectory = options.DataDirectory,
            Cluster = { SharedDirectory = SharedFolder },
        };
        var health = ActivatorUtilities.CreateInstance<SystemHealthService>(scope.ServiceProvider, Options.Create(api), new DataDirectories(api));
        return await health.BuildAsync(Ct);
    }

    private DataDirectories SharedDirectories()
    {
        var options = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        return new DataDirectories(new PlatformOptions { DataDirectory = options.DataDirectory, Cluster = { SharedDirectory = SharedFolder } });
    }

    private static string SharedFolder => Path.Combine(Path.GetTempPath(), "hc360-tests", "it-shared-health");

    /// <summary>The data folder of another server of several, of its own, as every server has.</summary>
    internal static DataDirectories NodeFolder() => new(new PlatformOptions
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "it-node-" + Guid.NewGuid().ToString("N")),
        Cluster = { SharedDirectory = SharedFolder },
    });

    /// <summary>The part an API node writes, with nothing to warn of.</summary>
    private static ServerNodeHealth ApiNode(string machine) => JobNode(machine, role: "api") with { Warnings = [] };

    /// <summary>A server's row as when it stopped writing it ten minutes ago, without saying so, as in a power cut.</summary>
    private Task SilenceAsync(string node) => SilenceAsync(node, "10 minutes");

    /// <summary>A server's row as when it stopped writing it <paramref name="ago"/> ago (a PostgreSQL interval).</summary>
    private async Task SilenceAsync(string node, string ago)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE platform.server_nodes SET reported_at = now() - CAST({ago} AS interval) WHERE node = {node}", Ct);
    }

    private const string Servers = "/api/v1/admin/system/servers";

    /// <summary>How many times <paramref name="machine"/> was removed from System health, as the audit trail has it.</summary>
    private async Task<int> RemovalsAsync(string machine)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>()
            .CountAsync(e => e.Action == ServerRemoval.RemovedAction && e.EntityId == machine, Ct);
    }

    /// <summary>One check of the alerts at <paramref name="now"/>, from <paramref name="report"/>, as the node that runs them makes it.</summary>
    private async Task AlertsAsync(SystemHealthReport report, DateTimeOffset now)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await SystemHealthAlerts.CheckAsync(report, scope.ServiceProvider.GetRequiredService<AlertIncidents>(), now, Ct);
    }

    /// <summary>The alert raised last for <paramref name="machine"/> not reporting.</summary>
    private async Task<AlertIncident> NotReportingAsync(string machine)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AlertIncident>().AsNoTracking()
            .Where(i => i.Code == SystemHealthService.NotReportingCode && i.Server == machine)
            .OrderByDescending(i => i.RaisedAt)
            .FirstAsync(Ct);
    }

    /// <summary>How many in the app were told that <paramref name="machine"/> reports again.</summary>
    private async Task<int> ToldBackAsync(string machine)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>()
            .CountAsync(n => n.Title == SystemHealthAlerts.BackTitle && n.Body!.Contains(machine), Ct);
    }

    private async Task<string?> ReportOfAsync(string node)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ServerNode>()
            .Where(n => n.Node == node)
            .Select(n => n.Report)
            .FirstOrDefaultAsync(Ct);
    }

    /// <summary>The part the node that runs the jobs writes: a Windows server whose data drive is nearly full.</summary>
    internal static ServerNodeHealth JobNode(string machine, string role = "worker") => new(
        new ServerInfo(machine, "Microsoft Windows 10.0.20348", "1.0.0", "Production", role, DateTimeOffset.UtcNow.AddDays(-3), 72,
            8, 900_000_000, ".NET 10.0.12"),
        new NetworkInfo(false, null, null, null, [], ["10.0.0.9"], 7360, 7361, false),
        new StorageInfo(@"C:\ProgramData\HyFib\HealthCare360", 20_000_000_000, 500_000_000_000),
        new CertificateInfo("AB:CD", DateTimeOffset.UtcNow.AddDays(300), ["hc360-jobs"], DateTimeOffset.UtcNow.AddYears(9)),
        new PowerInfo(true, false, 100, "UPS"),
        [new HealthWarning("disk.low", "critical", "Less than 10% disk space free on the data drive (18 GB).")],
        DateTimeOffset.UtcNow);
}
