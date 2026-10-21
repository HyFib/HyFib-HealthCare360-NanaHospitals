using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// The analysers' status as every server sees it (SCAL-01). With several servers each knows a part of it: the node that runs
/// the jobs listens for the network analysers (SCAL-05), and a lab agent's relay and heartbeats reach whichever API node
/// the proxy sends them to. Each server keeps its part in its <see cref="LabGatewayState"/> and writes it to
/// <c>integrations.lab_gateway_nodes</c> (<see cref="LabStatusSharing"/>), and reads the others' parts with its own, so
/// Lab setup and the integrations dashboard show the same status whichever server answers. A single Server Node has all
/// of it in its own memory, and nobody to write it for.
/// </summary>
internal sealed class LabStatusBoard(LabGatewayState state, LabRelayRegistry relays, IServiceScopeFactory scopes)
{
    /// <summary>
    /// How long a server's row counts after it was written: a server writes it again every minute. It is measured by the
    /// database's clock, as the row is written and as it is read, so servers whose clocks differ agree on it: by the
    /// writer's clock, a row from a server whose clock was minutes behind the reader's had ended before it was written.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    /// <summary>A row this long past its end belongs to a server that stopped without removing it (a power cut).</summary>
    private static readonly TimeSpan GoneAfter = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>This server's row, for as long as it runs.</summary>
    public string Node { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Changes with every change to this server's part.</summary>
    public long Version => state.Version;

    /// <summary>Writes this server's part for the others, or removes its row when it has none.</summary>
    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var statuses = state.Snapshot();
        var agents = relays.ConnectedAgents();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        if (statuses.Count > 0 || agents.Count > 0)
        {
            var statusJson = JsonSerializer.Serialize(statuses, Json);
            var agentJson = JsonSerializer.Serialize(agents, Json);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO integrations.lab_gateway_nodes (node, statuses, relaying_agents, expires_at)
                VALUES ({Node}, CAST({statusJson} AS jsonb), CAST({agentJson} AS jsonb), now() + {Lifetime})
                ON CONFLICT (node) DO UPDATE
                SET statuses = EXCLUDED.statuses, relaying_agents = EXCLUDED.relaying_agents, expires_at = EXCLUDED.expires_at
                """, cancellationToken);
        }
        else
        {
            await db.Set<LabGatewayNode>().Where(n => n.Node == Node).ExecuteDeleteAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM integrations.lab_gateway_nodes WHERE expires_at < now() - {GoneAfter}", cancellationToken);
    }

    /// <summary>Removes this server's row, as it stops.</summary>
    public async Task WithdrawAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<LabGatewayNode>()
            .Where(n => n.Node == Node)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Every server's status of the analysers, one per analyser, and the lab agents relaying through any server.</summary>
    public async Task<LabStatusView> ReadAsync(CancellationToken cancellationToken)
    {
        var statuses = state.Snapshot().ToList();
        var agents = relays.ConnectedAgents().ToHashSet();
        await using var scope = scopes.CreateAsyncScope();
        var others = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<LabGatewayNode>()
            .FromSql($"SELECT * FROM integrations.lab_gateway_nodes WHERE node <> {Node} AND expires_at > now()")
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            statuses.AddRange(JsonSerializer.Deserialize<List<LabListenerStatus>>(other.Statuses, Json) ?? []);
            agents.UnionWith(JsonSerializer.Deserialize<List<Guid>>(other.RelayingAgents, Json) ?? []);
        }

        return new LabStatusView(Merge(statuses), agents);
    }

    /// <summary>
    /// One status per analyser from what several servers say of it: the state that tells most (a relay's or its agent's
    /// over the gateway's, either over waiting), the latest of two that tell as much, and the counts of all of them.
    /// </summary>
    internal static IReadOnlyList<LabListenerStatus> Merge(IEnumerable<LabListenerStatus> statuses) =>
    [
        .. statuses.GroupBy(s => s.InstrumentCode, StringComparer.OrdinalIgnoreCase)
            .Select(said =>
            {
                var told = said.OrderByDescending(Weight).ThenByDescending(s => s.ChangedAt).First();
                var failed = said.Where(s => s.LastErrorAt is not null).MaxBy(s => s.LastErrorAt);
                return told with
                {
                    ActiveConnections = said.Sum(s => s.ActiveConnections),
                    MessagesAccepted = said.Sum(s => s.MessagesAccepted),
                    MessagesRefused = said.Sum(s => s.MessagesRefused),
                    LastMessageAt = said.Max(s => s.LastMessageAt),
                    LastErrorAt = failed?.LastErrorAt,
                    LastError = failed?.LastError,
                };
            })
            .OrderBy(s => s.InstrumentCode, StringComparer.OrdinalIgnoreCase),
    ];

    private static int Weight(LabListenerStatus status) => status.State switch
    {
        // The server holding the agent's relay, or the agent's heartbeat.
        "connected" or "port_error" => 3,

        // The node that runs the jobs: its listeners and the training analysers.
        "listening" or "failed" or "training" => 2,
        "waiting" => 1,
        _ => 0,
    };
}

/// <summary>The analysers' status on every server, and the lab agents relaying an analyser through any of them.</summary>
internal sealed record LabStatusView(IReadOnlyList<LabListenerStatus> Statuses, IReadOnlySet<Guid> RelayingAgents);

/// <summary>
/// Writes this server's analyser status for the other servers (<see cref="LabStatusBoard"/>) on a node of several: a few
/// seconds after it changes, and every minute so that it does not expire, and removes it as the server stops.
/// </summary>
internal sealed class LabStatusSharing(LabStatusBoard board, DataDirectories directories, TimeProvider time, ILogger<LabStatusSharing> logger)
    : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!directories.IsShared)
        {
            return;
        }

        long? shared = null;
        var sharedAt = DateTimeOffset.MinValue;
        var failing = false;
        using var timer = new PeriodicTimer(CheckEvery, time);
        try
        {
            do
            {
                var version = board.Version;
                if (version == shared && time.GetUtcNow() - sharedAt < RefreshEvery)
                {
                    continue;
                }

                try
                {
                    await board.PublishAsync(stoppingToken);
                    (shared, sharedAt, failing) = (version, time.GetUtcNow(), false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Said once while it fails, not every five seconds.
                    if (!failing)
                    {
                        logger.LogWarning(ex, "This server's analyser status could not be shared with the other servers; trying again");
                    }

                    failing = true;
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
        finally
        {
            using var withdrawing = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await board.WithdrawAsync(withdrawing.Token);
            }
            catch (Exception ex)
            {
                // Its row ends by itself within minutes.
                logger.LogDebug(ex, "This server's analyser status was not removed as it stopped");
            }
        }
    }
}
