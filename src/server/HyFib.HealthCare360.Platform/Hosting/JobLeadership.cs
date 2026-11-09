using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Hosting;

/// <summary>
/// The job lease (SCAL-05), in <c>HealthCare360:JobLeadership</c> of the settings file. With several servers on one
/// database, every server that may run the background jobs (<c>"Role": "all"</c> or <c>"worker"</c>) asks for the lease,
/// and only the one that holds it runs them; the others stand by and take over, one of them, within
/// <see cref="LeaseTimeout"/> and <see cref="CandidatePoll"/> of it going away.
/// </summary>
public sealed class JobLeadershipOptions
{
    public const string SectionName = PlatformOptions.SectionName + ":JobLeadership";

    /// <summary>
    /// Whether the servers that may run the jobs take turns at them through the lease. Unset, it is on wherever the servers
    /// share a folder (several servers, <see cref="DataDirectories.IsShared"/>), and off on a single Server Node, which
    /// runs them as before. Off with several servers, each one that may run the jobs runs them all the time.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>How often the server that holds the lease says so to the database.</summary>
    public TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the database keeps the lease of a server it has not heard from: its connection is closed then
    /// (PostgreSQL's <c>idle_session_timeout</c>), and with it the lease, whether the server lost its power, hangs, or
    /// cannot reach the database. The server itself stops its jobs two heartbeats before that.
    /// </summary>
    public TimeSpan LeaseTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How often a server standing by asks for the lease.</summary>
    public TimeSpan CandidatePoll { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether a server that loses the lease stops altogether (Windows starts its service again, and it stands by), rather
    /// than only stopping its jobs. The jobs' own services cannot all be started twice in one run, so off is for tests.
    /// </summary>
    public bool StopServerOnLoss { get; set; } = true;

    /// <summary>The longest a server may go without a heartbeat before it stops its jobs: two heartbeats before the database lets go.</summary>
    public TimeSpan FenceAfter => LeaseTimeout - (2 * Heartbeat);

    /// <summary>What is wrong with the timings, in words for whoever edits the settings file.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Heartbeat < TimeSpan.FromMilliseconds(100) || CandidatePoll < TimeSpan.FromMilliseconds(100))
        {
            problems.Add($"{SectionName}: Heartbeat and CandidatePoll must each be at least 100 ms.");
        }

        if (LeaseTimeout < 3 * Heartbeat)
        {
            problems.Add($"{SectionName}: LeaseTimeout ({LeaseTimeout}) must be at least three Heartbeats ({3 * Heartbeat}), so the server holding the lease stops its jobs before the database lets another take it.");
        }

        return problems;
    }
}

/// <summary>
/// A hosted service that runs on every server whatever its role, and is never held back for the job lease
/// (<see cref="JobLeadership"/>): the server's own part of the cluster (its cache signals, its part of System health, its
/// clock check, its mDNS announcements) rather than the hospital's background work.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NodeLocalAttribute : Attribute;

/// <summary>Whether this server runs the background jobs now, and since when; null where there is no lease (its role decides).</summary>
public sealed record JobNodeState(bool Active, DateTimeOffset? Since);

/// <summary>
/// Which one of several servers runs the background jobs (SCAL-05): the one holding a PostgreSQL advisory lock on a
/// connection of its own, which it says something on every <see cref="JobLeadershipOptions.Heartbeat"/>. The connection
/// is closed by PostgreSQL once it has been quiet for <see cref="JobLeadershipOptions.LeaseTimeout"/>
/// (<c>idle_session_timeout</c>), so the lock is released whether the server stopped, lost its power or its network, or
/// hangs: no table, no clock but the database's, and the database decides alone who holds it.
/// <para>
/// The others ask for it every <see cref="JobLeadershipOptions.CandidatePoll"/>. The one that gets it starts the jobs'
/// hosted services, which the server held back (<see cref="JobLeadershipServiceCollectionExtensions.AddJobLeadership"/>),
/// a <see cref="JobLeadershipOptions.LeaseTimeout"/> later. The one that holds it stops them as soon as a heartbeat fails,
/// and at the latest <see cref="JobLeadershipOptions.FenceAfter"/> after the last that succeeded; then the server stops
/// (Windows starts it again, to stand by). So two servers never run the jobs at once, even when the database lets the
/// lease go early, and once the holder has gone another runs them within <c>2 × LeaseTimeout + CandidatePoll</c>
/// (45 seconds by default).
/// </para>
/// <para>
/// A job that starts on a server that has just taken over finds what the last one did in the database, as after a restart
/// of the job node: the jobs keep what they did there (the ledger, the messages sent, each run's record). Jobs Quartz
/// fires at set times also claim each time once for the hospital (<see cref="JobSlotClaim"/>).
/// </para>
/// </summary>
public sealed class JobLeadership : BackgroundService
{
    /// <summary>The advisory lock (two keys): HC360's namespace for it, and the job lease within it.</summary>
    internal const int LockNamespace = 0x4843_4A42;

    internal const int LockKey = 1;

    /// <summary>The connection's application name, after which the node id: how System health reads who holds the lease.</summary>
    internal const string ApplicationNamePrefix = "HC360 jobs ";

    private readonly IServiceProvider _services;
    private readonly JobLeadershipOptions _options;
    private readonly ILogger<JobLeadership> _logger;
    private readonly TimeProvider _time;
    private readonly List<GatedHostedService> _gated = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _fenced = new();
    private readonly SemaphoreSlim _switching = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private NpgsqlDataSource? _dataSource;
    private volatile bool _active;
    private DateTimeOffset? _activeSince;
    private bool _jobsStarted;
    private long _lastHeartbeat;
    private int _lost;

    internal JobLeadership(IServiceProvider services, JobLeadershipOptions options, bool leasing, bool runsJobs)
    {
        _services = services;
        _options = options;
        Leasing = leasing;
        RunsJobs = runsJobs;
        _logger = services.GetRequiredService<ILogger<JobLeadership>>();
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _active = runsJobs && !leasing;
    }

    /// <summary>Whether this server takes turns at the jobs through the lease.</summary>
    public bool Leasing { get; }

    /// <summary>Whether this server may run the jobs at all (its role).</summary>
    public bool RunsJobs { get; }

    /// <summary>Whether this server runs the background jobs now: it holds the lease, or runs them by its role alone.</summary>
    public bool IsActive => _active;

    /// <summary>What System health says of this server's part in the jobs; null without the lease.</summary>
    public JobNodeState? State => Leasing ? new JobNodeState(_active, _activeSince) : null;

    /// <summary>
    /// The node id of the server holding the lease, as the database has it now; null when none does. Read from the
    /// lock itself (<c>pg_locks</c>) and the holder's connection, so it is right the moment the lease changes hands.
    /// </summary>
    public static async Task<string?> ReadHolderAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            return await ReadHolderAsync(connection, cancellationToken);
        }
        finally
        {
            if (opened)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    /// <inheritdoc cref="ReadHolderAsync(HospitalDbContext, CancellationToken)"/>
    public static async Task<string?> ReadHolderAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var read = new NpgsqlCommand("""
            SELECT a.application_name
            FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
            WHERE l.locktype = 'advisory' AND l.granted AND l.objsubid = 2
              AND l.classid = @namespace::oid AND l.objid = @key::oid
              AND l.database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """, connection);
        read.Parameters.AddWithValue("namespace", (long)LockNamespace);
        read.Parameters.AddWithValue("key", (long)LockKey);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0) && reader.GetString(0) is { } name && name.StartsWith(ApplicationNamePrefix, StringComparison.Ordinal))
            {
                return name[ApplicationNamePrefix.Length..];
            }
        }

        return null;
    }

    /// <summary>Whether a hosted service of this type is one of the background jobs, held back for the lease.</summary>
    internal static bool IsJob(Type type)
    {
        if (type.GetCustomAttribute<NodeLocalAttribute>() is not null || type == typeof(JobLeadership))
        {
            return false;
        }

        var assembly = type.Assembly.GetName().Name ?? string.Empty;
        return assembly.StartsWith("HyFib.HealthCare360", StringComparison.Ordinal) || assembly.StartsWith("Quartz", StringComparison.Ordinal);
    }

    internal void Enlist(GatedHostedService service)
    {
        lock (_gate)
        {
            _gated.Add(service);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The jobs stop before the lease is let go (base.StopAsync ends the connection that holds it), and none starts meanwhile.
        await _shutdown.CancelAsync();
        await StopJobsAsync(TimeSpan.FromSeconds(30));
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _dataSource?.Dispose();
        _fenced.Dispose();
        _switching.Dispose();
        _shutdown.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Leasing)
        {
            return;
        }

        // Every hosted service has started (the jobs' have enlisted) once the host has.
        var lifetime = _services.GetRequiredService<IHostApplicationLifetime>();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.ApplicationStarted).WaitAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStarted.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _dataSource = BuildDataSource();
        var node = _services.GetRequiredService<ServerNodeBoard>().Node;
        _logger.LogInformation("This server stands by to run the background jobs; it runs them once it holds the job lease");
        var failing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            NpgsqlConnection? held = null;
            try
            {
                held = await TryAcquireAsync(node, stoppingToken);
                failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                if (!failing)
                {
                    _logger.LogWarning(ex, "Could not ask the database for the job lease; asking again");
                }

                failing = true;
            }

            if (held is not null)
            {
                await using (held)
                {
                    await LeadAsync(held, stoppingToken);
                }

                return;
            }

            try
            {
                await Task.Delay(_options.CandidatePoll, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private NpgsqlDataSource BuildDataSource()
    {
        var configuration = _services.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(ClusterSignals.ConnectionName) is { Length: > 0 } own
            ? own
            : configuration.GetConnectionString("Hospital") ?? throw new InvalidOperationException("Connection string 'Hospital' is not configured.");
        var (connection, supplyPassword) = _services.GetRequiredService<DatabaseCredentials>().For(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connection);

        // A connection of its own, never pooled: closing it must end the session, and the lock with it.
        builder.ConnectionStringBuilder.Pooling = false;
        builder.ConnectionStringBuilder.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_options.Heartbeat.TotalSeconds));
        builder.ConnectionStringBuilder.Timeout = Math.Max(1, (int)Math.Ceiling(_options.CandidatePoll.TotalSeconds));
        builder.ConnectionStringBuilder.ApplicationName = ApplicationNamePrefix + _services.GetRequiredService<ServerNodeBoard>().Node;
        supplyPassword(builder);
        return builder.Build();
    }

    /// <summary>A connection holding the lock, its session to end once it is quiet for the lease's time; null when another holds it.</summary>
    private async Task<NpgsqlConnection?> TryAcquireAsync(string node, CancellationToken cancellationToken)
    {
        var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        try
        {
            await using (var set = new NpgsqlCommand("SELECT set_config('idle_session_timeout', @timeout, false)", connection))
            {
                set.Parameters.AddWithValue("timeout", ((long)_options.LeaseTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
                await set.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(@namespace, @key)", connection);
            take.Parameters.AddWithValue("namespace", LockNamespace);
            take.Parameters.AddWithValue("key", LockKey);
            if (await take.ExecuteScalarAsync(cancellationToken) is true)
            {
                _logger.LogInformation("This server took the job lease (node {Node})", node);
                return connection;
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();
        return null;
    }

    /// <summary>
    /// Holds the lease: says so every heartbeat, starts the jobs once the last holder's can no longer be running, and stops
    /// them, and the server, as soon as it may have lost it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A heartbeat counts from when it was sent: the database cannot have heard from the server later than that, so it
    /// keeps the lease at least <see cref="JobLeadershipOptions.LeaseTimeout"/> from then, by its own clock, while the
    /// server stops its jobs <see cref="JobLeadershipOptions.FenceAfter"/> from then, by its own, and has them stopped one
    /// heartbeat later or stops dead. Only how fast the clocks run matters, never what time they show.
    /// </para>
    /// <para>
    /// The database can also let the lease go at once, without the holder knowing yet: PostgreSQL restarted, or the
    /// holder's connection ended by hand. So a server that has just taken the lease waits a whole
    /// <see cref="JobLeadershipOptions.LeaseTimeout"/> before it starts the jobs: by then the last holder, which heard
    /// nothing back from the database since, has stopped its own.
    /// </para>
    /// </remarks>
    private async Task LeadAsync(NpgsqlConnection held, CancellationToken stoppingToken)
    {
        Interlocked.Exchange(ref _lastHeartbeat, Stopwatch.GetTimestamp());
        using var leading = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _fenced.Token);
        var watchdog = WatchAsync(leading.Token);
        using var startable = CancellationTokenSource.CreateLinkedTokenSource(leading.Token, _shutdown.Token);
        var starting = StartAfterLastHolderAsync(startable.Token);

        while (!leading.IsCancellationRequested)
        {
            var sent = Stopwatch.GetTimestamp();
            try
            {
                if (BeforeHeartbeat is { } hook)
                {
                    await hook(leading.Token);
                }

                await using var beat = new NpgsqlCommand("SELECT 1", held);
                await beat.ExecuteScalarAsync(leading.Token);
                Interlocked.Exchange(ref _lastHeartbeat, sent);
                await Task.Delay(_options.Heartbeat, _time, leading.Token);
            }
            catch (OperationCanceledException) when (leading.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                await LoseAsync("a heartbeat to the database failed: " + ex.Message);
                break;
            }
        }

        await leading.CancelAsync();
        await starting;
        await watchdog;
    }

    /// <summary>For tests: what each heartbeat waits for before it is sent, as a server that hangs or lost the database would.</summary>
    internal Func<CancellationToken, Task>? BeforeHeartbeat { get; set; }

    private async Task StartAfterLastHolderAsync(CancellationToken startable)
    {
        try
        {
            _logger.LogInformation("This server holds the job lease: it starts the background jobs in {Seconds:0.#} s, once the last holder's have stopped",
                _options.LeaseTimeout.TotalSeconds);
            await Task.Delay(_options.LeaseTimeout, _time, startable);
            await StartJobsAsync(startable);
        }
        catch (OperationCanceledException) when (startable.IsCancellationRequested)
        {
        }
    }

    /// <summary>Fences the jobs off once no heartbeat has succeeded for <see cref="JobLeadershipOptions.FenceAfter"/>, whatever the heartbeat itself is waiting for.</summary>
    private async Task WatchAsync(CancellationToken leading)
    {
        var every = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond * 20, _options.Heartbeat.Ticks / 10));
        try
        {
            while (!leading.IsCancellationRequested)
            {
                await Task.Delay(every, _time, leading);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastHeartbeat)) >= _options.FenceAfter)
                {
                    await LoseAsync($"no heartbeat reached the database for {_options.FenceAfter.TotalSeconds:0.#} s");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Stops the jobs, once, as soon as the lease may be lost, and then the server. Jobs that have not stopped a heartbeat
    /// later are not waited for: with <see cref="JobLeadershipOptions.StopServerOnLoss"/> the server stops dead, as the
    /// next holder starts them a <see cref="JobLeadershipOptions.LeaseTimeout"/> after it took the lease.
    /// </summary>
    private async Task LoseAsync(string why)
    {
        if (Interlocked.Exchange(ref _lost, 1) == 1)
        {
            return;
        }

        _active = false;
        await _fenced.CancelAsync();
        _logger.LogCritical(
            "This server may no longer hold the job lease ({Reason}): it stops its background jobs now, before another server can take them over{Then}",
            why, _options.StopServerOnLoss ? ", and then stops; Windows starts it again, to stand by" : string.Empty);
        var stopped = await StopJobsAsync(_options.Heartbeat);
        if (!_options.StopServerOnLoss)
        {
            return;
        }

        Environment.ExitCode = 1;
        if (!stopped)
        {
            Environment.FailFast("HC360 lost the job lease and its background jobs did not stop in time: stopping now so that two servers never run them at once.");
        }

        _services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
    }

    private async Task StartJobsAsync(CancellationToken cancellationToken)
    {
        await _switching.WaitAsync(cancellationToken);
        try
        {
            GatedHostedService[] gated;
            lock (_gate)
            {
                gated = [.. _gated];
                _jobsStarted = true;
            }

            _activeSince = _time.GetUtcNow();
            _active = true;
            _logger.LogInformation("This server runs the background jobs from now on");
            foreach (var service in gated)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await service.StartJobAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The background job {Job} did not start", service.Name);
                }
            }
        }
        finally
        {
            _switching.Release();
        }
    }

    /// <summary>Stops every job started; whether they all stopped within <paramref name="within"/>.</summary>
    private async Task<bool> StopJobsAsync(TimeSpan within)
    {
        _active = false;
        using var timeout = new CancellationTokenSource(within);
        var waited = Stopwatch.GetTimestamp();

        // A start under way gives up at the next job (the lease's token is cancelled first), then this stops what it started.
        await _switching.WaitAsync();
        try
        {
            GatedHostedService[] gated;
            lock (_gate)
            {
                if (!_jobsStarted)
                {
                    return true;
                }

                gated = [.. _gated];
                _jobsStarted = false;
            }

            var left = within - Stopwatch.GetElapsedTime(waited);
            var stopping = Task.WhenAll(gated.Reverse().Select(async service =>
            {
                try
                {
                    await service.StopJobAsync(timeout.Token);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The background job {Job} did not stop cleanly", service.Name);
                }
            }));
            return await Task.WhenAny(stopping, Task.Delay(left > TimeSpan.Zero ? left : TimeSpan.Zero)) == stopping;
        }
        finally
        {
            _switching.Release();
        }
    }
}

/// <summary>Registers the job lease (<see cref="JobLeadership"/>).</summary>
public static class JobLeadershipServiceCollectionExtensions
{
    /// <summary>
    /// Holds back the background jobs' hosted services on a server that takes turns at them: each starts only once this
    /// server holds the lease. Call it after every hosted service is registered. A server that does not take turns (a
    /// single Server Node, an API node, the lease off) is left exactly as it was.
    /// </summary>
    public static IServiceCollection AddJobLeadership(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<JobLeadershipOptions>().Bind(configuration.GetSection(JobLeadershipOptions.SectionName));
        var options = configuration.GetSection(JobLeadershipOptions.SectionName).Get<JobLeadershipOptions>() ?? new JobLeadershipOptions();
        var platform = configuration.GetSection(PlatformOptions.SectionName).Get<PlatformOptions>() ?? new PlatformOptions();
        var shared = services.Select(d => d.IsKeyedService ? null : d.ImplementationInstance).OfType<DataDirectories>().LastOrDefault()?.IsShared == true;
        var leasing = platform.RunsJobs && (options.Enabled ?? shared);
        if (leasing && options.Problems() is { Count: > 0 } problems)
        {
            throw new InvalidOperationException("HC360 cannot start: " + string.Join(" ", problems));
        }

        services.AddSingleton(sp => new JobLeadership(sp, options, leasing, platform.RunsJobs));
        if (!leasing)
        {
            return services;
        }

        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService) && !descriptor.IsKeyedService)
            {
                services[i] = ServiceDescriptor.Singleton<IHostedService>(sp => new GatedHostedService(descriptor, sp, sp.GetRequiredService<JobLeadership>()));
            }
        }

        // Last, so that it stops first: the jobs stop before the lease is let go.
        services.AddHostedService(sp => sp.GetRequiredService<JobLeadership>());
        return services;
    }
}

/// <summary>
/// A hosted service as registered, held back on a server that takes turns at the jobs (<see cref="JobLeadership"/>): a
/// background job starts once the server holds the lease and stops when it lets it go; one of the server's own
/// (<see cref="NodeLocalAttribute"/>, or the framework's) starts and stops with the host, as before.
/// </summary>
internal sealed class GatedHostedService : IHostedService, IAsyncDisposable
{
    private readonly IHostedService _inner;
    private readonly bool _owned;
    private readonly JobLeadership _leadership;
    private readonly bool _job;
    private int _running;

    public GatedHostedService(ServiceDescriptor registered, IServiceProvider services, JobLeadership leadership)
    {
        _leadership = leadership;
        (_inner, _owned) = registered switch
        {
            { ImplementationInstance: IHostedService instance } => (instance, false),
            { ImplementationFactory: { } factory } => ((IHostedService)factory(services), false),
            _ => ((IHostedService)ActivatorUtilities.CreateInstance(services, registered.ImplementationType!), true),
        };
        _job = JobLeadership.IsJob(_inner.GetType());
    }

    /// <summary>The hosted service held back.</summary>
    public IHostedService Inner => _inner;

    public string Name => _inner.GetType().Name;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_job)
        {
            _leadership.Enlist(this);
            return;
        }

        await _inner.StartAsync(cancellationToken);
        Interlocked.Exchange(ref _running, 1);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        Interlocked.Exchange(ref _running, 0) == 1 ? _inner.StopAsync(cancellationToken) : Task.CompletedTask;

    internal async Task StartJobAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            await _inner.StartAsync(cancellationToken);
        }
    }

    internal Task StopJobAsync(CancellationToken cancellationToken) => StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!_owned)
        {
            return;
        }

        switch (_inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
