using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The job lease (SCAL-05): which hosted services it holds back, when it is on, the timings it accepts, what System health
/// makes of the servers' states, and the claim that runs each of Quartz's due times once.
/// </summary>
public sealed class JobLeadershipTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_hospitals_background_jobs_are_held_back_and_each_servers_own_services_are_not()
    {
        JobLeadership.IsJob(typeof(FakeJob)).ShouldBeTrue("HC360's hosted services are jobs unless marked otherwise");
        JobLeadership.IsJob(typeof(FakeNodeService)).ShouldBeFalse("a [NodeLocal] service runs on every server");
        JobLeadership.IsJob(typeof(BackgroundService)).ShouldBeFalse("the framework's own (the web server, Windows' service host) run on every server");
        JobLeadership.IsJob(typeof(JobLeadership)).ShouldBeFalse();
    }

    [Fact]
    public void A_single_server_or_an_API_node_is_left_as_it_was()
    {
        foreach (var settings in new[]
        {
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["HealthCare360:JobLeadership:Enabled"] = "true", ["HealthCare360:Role"] = "api" },
            new Dictionary<string, string?> { ["HealthCare360:JobLeadership:Enabled"] = "false" },
        })
        {
            var services = Services();
            var before = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
            services.AddJobLeadership(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            services.Where(d => d.ServiceType == typeof(IHostedService)).ShouldBe(before);
        }
    }

    [Fact]
    public async Task With_the_lease_every_hosted_service_waits_for_it_and_the_servers_own_start_at_once()
    {
        var services = Services();
        services.AddJobLeadership(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HealthCare360:JobLeadership:Enabled"] = "true" }).Build());
        services.Last().ImplementationFactory.ShouldNotBeNull("the lease itself is added last, so it stops first");
        await using var provider = services.BuildServiceProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();
        hosted.Count.ShouldBe(3);
        hosted[^1].ShouldBeOfType<JobLeadership>();
        foreach (var service in hosted.OfType<GatedHostedService>())
        {
            await service.StartAsync(Ct);
        }

        var gated = hosted.OfType<GatedHostedService>().ToDictionary(g => g.Inner.GetType());
        ((FakeJob)gated[typeof(FakeJob)].Inner).Started.ShouldBeFalse("a job waits for the lease");
        ((FakeNodeService)gated[typeof(FakeNodeService)].Inner).Started.ShouldBeTrue("the server's own service starts with the host");
        provider.GetRequiredService<JobLeadership>().IsActive.ShouldBeFalse();
        provider.GetRequiredService<JobLeadership>().State.ShouldBe(new JobNodeState(false, null));
    }

    [Fact]
    public void Timings_that_would_let_two_servers_run_the_jobs_are_refused()
    {
        new JobLeadershipOptions().Problems().ShouldBeEmpty();
        new JobLeadershipOptions().FenceAfter.ShouldBe(TimeSpan.FromSeconds(10));
        new JobLeadershipOptions { Heartbeat = TimeSpan.FromSeconds(10), LeaseTimeout = TimeSpan.FromSeconds(20) }.Problems()
            .ShouldHaveSingleItem().ShouldContain("at least three Heartbeats");

        var services = Services();
        Should.Throw<InvalidOperationException>(() => services.AddJobLeadership(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HealthCare360:JobLeadership:Enabled"] = "true",
                ["HealthCare360:JobLeadership:LeaseTimeout"] = "00:00:05",
            }).Build()));
    }

    [Fact]
    public void System_health_takes_the_lease_holder_as_the_one_that_runs_the_jobs()
    {
        var holder = Node("A", "all", new JobNodeState(true, DateTimeOffset.UtcNow));
        var standing = Node("B", "all", new JobNodeState(false, null));
        SystemHealthService.ServerWarnings([holder, standing], DateTimeOffset.UtcNow).ShouldBeEmpty();

        // Between one holder and the next: a warning, not a critical one, while another stands by.
        var between = SystemHealthService.ServerWarnings([Node("A", "all", new JobNodeState(false, null)), standing], DateTimeOffset.UtcNow);
        between.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            w => w.Code.ShouldBe("server.jobs_taking_over"),
            w => w.Severity.ShouldBe("warning"));

        // The holder went silent: still critical, but it says who takes over.
        var silent = SystemHealthService.ServerWarnings([holder with { Reporting = false }, standing], DateTimeOffset.UtcNow);
        silent.Single(w => w.Code == "server.not_reporting").ShouldSatisfyAllConditions(
            w => w.Severity.ShouldBe("critical"),
            w => w.Message.ShouldContain("B stands by to take the jobs over"));

        // Two holders cannot be: critical.
        SystemHealthService.ServerWarnings([holder, Node("B", "all", new JobNodeState(true, DateTimeOffset.UtcNow))], DateTimeOffset.UtcNow)
            .Single(w => w.Code == "server.several_jobs").Severity.ShouldBe("critical");
    }

    [Fact]
    public async Task Each_due_time_of_a_scheduled_job_runs_once_for_the_hospital()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new MemoryClusterStore(cache, TimeProvider.System);
        ITriggerListener first = new JobSlotClaim(store, NullLogger<JobSlotClaim>.Instance);
        ITriggerListener second = new JobSlotClaim(store, NullLogger<JobSlotClaim>.Instance);
        var due = new DateTimeOffset(2026, 11, 9, 20, 30, 0, TimeSpan.Zero);

        (await first.VetoJobExecution(Trigger(), Fire(due), Ct)).ShouldBeFalse("the first server to fire it runs it");
        (await second.VetoJobExecution(Trigger(), Fire(due), Ct)).ShouldBeTrue("another server firing the same due time does not");
        (await second.VetoJobExecution(Trigger(), Fire(due.AddDays(1)), Ct)).ShouldBeFalse("the next night's is a due time of its own");

        var down = Substitute.For<IClusterStore>();
        down.TryAddAsync(default!, default, Ct).ReturnsForAnyArgs<bool>(_ => throw new TimeoutException());
        (await ((ITriggerListener)new JobSlotClaim(down, NullLogger<JobSlotClaim>.Instance)).VetoJobExecution(Trigger(), Fire(due.AddDays(2)), Ct))
            .ShouldBeTrue("a due time that cannot be claimed is not run");
    }

    /// <summary>
    /// Quartz takes the claim as a trigger listener, as the platform registers it: Quartz 4 refuses, at registration, a
    /// listener with a member that matches an interface member by name but not by signature (one Quartz would never call).
    /// </summary>
    [Fact]
    public async Task Quartz_takes_the_claim_as_a_trigger_listener()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClusterStore>(_ => new MemoryClusterStore(new MemoryCache(new MemoryCacheOptions()), TimeProvider.System));
        services.AddQuartz(quartz => quartz.AddTriggerListener<JobSlotClaim>([GroupMatcher<TriggerKey>.AnyGroup()]));
        await using var provider = services.BuildServiceProvider();

        var scheduler = await provider.GetRequiredService<ISchedulerFactory>().GetScheduler(Ct);
        scheduler.ListenerManager.GetTriggerListeners().ShouldContain(l => l is JobSlotClaim);
        await scheduler.Shutdown(false, Ct);
    }

    private static ITrigger Trigger()
    {
        var trigger = Substitute.For<ITrigger>();
        trigger.Key.Returns(new TriggerKey("nightly-backup"));
        return trigger;
    }

    private static IJobExecutionContext Fire(DateTimeOffset due)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.ScheduledFireTimeUtc.Returns(due);
        context.FireTimeUtc.Returns(due.AddSeconds(1));
        return context;
    }

    private static ServerNodeHealth Node(string name, string role, JobNodeState? jobs) => new(
        new ServerInfo(name, "Windows", "1.0", "Production", role, DateTimeOffset.UtcNow, 1, 4, 1, ".NET", Node: name, Jobs: jobs),
        new NetworkInfo(false, null, null, null, [], [], 443, 7361, false),
        new StorageInfo("D:", 100, 100),
        new CertificateInfo(null, null, []),
        new PowerInfo(false, null, null, null),
        [],
        DateTimeOffset.UtcNow);

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostedService<FakeJob>();
        services.AddHostedService(_ => new FakeNodeService());
        return services;
    }

    private sealed class FakeJob : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [NodeLocal]
    private sealed class FakeNodeService : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
