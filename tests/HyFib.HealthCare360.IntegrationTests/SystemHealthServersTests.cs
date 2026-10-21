using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
