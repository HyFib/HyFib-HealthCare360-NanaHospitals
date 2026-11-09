using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SCAL-05 with several servers that may each run the background jobs: exactly one runs them at a time, the one
/// holding the job lease (<see cref="JobLeadership"/>), and when it goes, another takes over within a bounded time, never
/// while the last one may still run them. Each server here is a host with the lease and one job, which records each run in
/// the database by the database's clock, so the runs of two servers can be set side by side. The timings are short
/// (heartbeat 400 ms, lease 2 s), as the settings allow.
/// </summary>
public sealed class JobLeadershipTests : IAsyncLifetime
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);

    private TestDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(Ct);
        await ExecuteAsync("CREATE TABLE job_runs (node text NOT NULL, at timestamptz NOT NULL DEFAULT clock_timestamp())");
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Of_two_servers_only_the_one_holding_the_lease_runs_the_jobs()
    {
        using var first = await StartAsync("first");
        using var second = await StartAsync("second");

        var holder = await HolderAsync(first, second);
        await Task.Delay(Lease, Ct);

        (await NodesThatRanAsync()).ShouldBe([holder.Name], "only the server holding the lease runs the jobs");
        first.Jobs.IsActive.ShouldNotBe(second.Jobs.IsActive);
        (await ReadHolderAsync()).ShouldBe(holder.Id, "the database says who holds the lease");
    }

    /// <summary>
    /// The database ends the holder's connection (PostgreSQL restarted, or someone ended it): the lease is free at once,
    /// before the holder knows. The other server takes it, and starts the jobs only once the holder must have stopped its
    /// own; the holder stops its jobs and then itself (Windows would start it again, to stand by).
    /// </summary>
    [Fact]
    public async Task When_the_database_ends_the_holders_connection_the_other_takes_over_without_running_the_jobs_twice()
    {
        using var first = await StartAsync("first");
        var holder = await HolderAsync(first);
        using var other = await StartAsync("second");
        await RunsAsync(holder.Name);

        await ExecuteAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = '{JobLeadership.ApplicationNamePrefix}{holder.Id}'");

        await TakesOverAsync(holder, other, within: (2 * Lease) + TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The holder hangs, or loses the database while its jobs go on (its heartbeats stop reaching it): it stops its jobs
    /// itself before the database lets the lease go, and the other takes over.
    /// </summary>
    [Fact]
    public async Task When_the_holder_stops_answering_it_stops_its_jobs_and_the_other_takes_over()
    {
        using var first = await StartAsync("first");
        var holder = await HolderAsync(first);
        using var other = await StartAsync("second");
        await RunsAsync(holder.Name);

        holder.Jobs.BeforeHeartbeat = ct => Task.Delay(Timeout.Infinite, ct);

        await TakesOverAsync(holder, other, within: (3 * Lease) + TimeSpan.FromSeconds(3));
    }

    /// <summary>A holder stopped on purpose stops its jobs and lets the lease go; the other takes over.</summary>
    [Fact]
    public async Task When_the_holder_is_stopped_the_other_takes_over()
    {
        using var first = await StartAsync("first");
        var holder = await HolderAsync(first);
        using var other = await StartAsync("second");
        await RunsAsync(holder.Name);

        await holder.Host.StopAsync(Ct);

        await TakesOverAsync(holder, other, within: (2 * Lease) + TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// The other server holds the lease and runs the jobs within the bound; the last holder ran none after the new one
    /// started (no slot run twice), and stopped itself.
    /// </summary>
    private async Task TakesOverAsync(Node last, Node next, TimeSpan within)
    {
        (await SecondNode.EventuallyAsync(() => Task.FromResult(next.Jobs.IsActive), within, Ct))
            .ShouldBeTrue($"the other server should run the jobs within {within.TotalSeconds:0.#} s");
        await RunsAsync(next.Name);
        last.Jobs.IsActive.ShouldBeFalse();
        last.Lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue("a server that lost the lease, or was stopped, stops");

        var lastRun = await ScalarAsync<DateTime>($"SELECT max(at) FROM job_runs WHERE node = '{last.Name}'");
        var firstRun = await ScalarAsync<DateTime>($"SELECT min(at) FROM job_runs WHERE node = '{next.Name}'");
        lastRun.ShouldBeLessThan(firstRun, "the new holder starts the jobs only after the last one ran its last");
        (await ReadHolderAsync()).ShouldBe(next.Id);
    }

    private async Task<Node> StartAsync(string name)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Logging.ClearProviders();
        var folder = Path.Combine(Path.GetTempPath(), "hc360-tests", "job-lease-" + Guid.NewGuid().ToString("N"));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Hospital"] = _database.ConnectionString,
            ["HealthCare360:DataDirectory"] = folder,
            ["HealthCare360:JobLeadership:Enabled"] = "true",
            ["HealthCare360:JobLeadership:Heartbeat"] = "00:00:00.400",
            ["HealthCare360:JobLeadership:LeaseTimeout"] = Lease.ToString("c", CultureInfo.InvariantCulture),
            ["HealthCare360:JobLeadership:CandidatePoll"] = "00:00:00.200",
        });
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = folder });
        directories.EnsureCreated();
        builder.Services.AddSingleton(directories);
        builder.Services.AddSingleton(new DatabaseCredentials(_database.ConnectionString));
        builder.Services.AddSingleton<ServerNodeBoard>();
        builder.Services.AddHostedService(_ => new RecordingJob(name, _database.ConnectionString));
        builder.Services.AddJobLeadership(builder.Configuration);

        var host = builder.Build();
        await host.StartAsync(Ct);
        return new Node(name, host);
    }

    /// <summary>Which of <paramref name="nodes"/> holds the lease and runs the jobs, once one does.</summary>
    private static async Task<Node> HolderAsync(params Node[] nodes)
    {
        (await SecondNode.EventuallyAsync(() => Task.FromResult(nodes.Any(n => n.Jobs.IsActive)), (2 * Lease) + TimeSpan.FromSeconds(5), Ct))
            .ShouldBeTrue("one server should take the lease and run the jobs");
        return nodes.Single(n => n.Jobs.IsActive);
    }

    private async Task RunsAsync(string node) =>
        (await SecondNode.EventuallyAsync(async () => await ScalarAsync<long>($"SELECT count(*) FROM job_runs WHERE node = '{node}'") > 0, TimeSpan.FromSeconds(10), Ct))
            .ShouldBeTrue($"{node} should run the job");

    private async Task<string[]> NodesThatRanAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT DISTINCT node FROM job_runs", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var nodes = new List<string>();
        while (await reader.ReadAsync(Ct))
        {
            nodes.Add(reader.GetString(0));
        }

        return [.. nodes];
    }

    /// <summary>The holder's node id, read from the lock as System health reads it.</summary>
    private async Task<string?> ReadHolderAsync()
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        return await JobLeadership.ReadHolderAsync(connection, Ct);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class Node(string name, IHost host) : IDisposable
    {
        public string Name => name;

        public IHost Host => host;

        public JobLeadership Jobs => host.Services.GetRequiredService<JobLeadership>();

        public string Id => host.Services.GetRequiredService<ServerNodeBoard>().Node;

        public IHostApplicationLifetime Lifetime => host.Services.GetRequiredService<IHostApplicationLifetime>();

        public void Dispose()
        {
            using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                host.StopAsync(stopping.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            host.Dispose();
        }
    }

    /// <summary>A background job: a run every 50 ms, each recorded with the database's clock.</summary>
    private sealed class RecordingJob(string node, string connectionString) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await using (var run = new NpgsqlCommand("INSERT INTO job_runs (node) VALUES (@node)", connection))
                {
                    run.Parameters.AddWithValue("node", node);
                    await run.ExecuteNonQueryAsync(stoppingToken);
                }

                await Task.Delay(50, stoppingToken);
            }
        }
    }
}
