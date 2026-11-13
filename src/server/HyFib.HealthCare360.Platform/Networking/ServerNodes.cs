using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// One server's part of System health while there are several (SCAL-01): <c>platform.server_nodes</c>, written by
/// <see cref="ServerNodeBoard"/>. A server writes its row every minute and removes it as it stops. One that stopped without
/// removing it (a power cut) keeps its row, which shows that it no longer reports, and its alert is told every day, until
/// it starts again and writes over it, it is set up again in the same place (<see cref="ServerNodeBoard.PublishAsync"/>),
/// or someone removes it (<see cref="ServerRemoval"/>). It used to go after a day, and its alert cleared unseen.
/// </summary>
[NotAudited]
public sealed class ServerNode
{
    /// <summary>The server's id, kept in its own data folder (<see cref="ServerNodeBoard.Node"/>).</summary>
    public string Node { get; set; } = string.Empty;

    /// <summary>What it reports of itself, as JSON: a <see cref="ServerNodeHealth"/>.</summary>
    public string Report { get; set; } = "{}";

    /// <summary>When it last wrote it, by the database's clock.</summary>
    public DateTimeOffset ReportedAt { get; set; }
}

internal sealed class ServerNodeConfiguration : IEntityTypeConfiguration<ServerNode>
{
    public void Configure(EntityTypeBuilder<ServerNode> b)
    {
        b.ToTable("server_nodes", "platform");
        b.HasKey(x => x.Node);
        b.Property(x => x.Node).HasMaxLength(64);
        b.Property(x => x.Report).HasColumnType("jsonb");
    }
}

/// <summary>
/// Every server's own part of System health when there are several (SCAL-01): the API node that answers reads the others'
/// with its own, so the page shows every server, the node that runs the jobs (which answers no requests) among them, and
/// the same whichever node answers. Each writes what it reports of its own machine
/// (<see cref="SystemHealthService.BuildServerAsync"/>) every minute (<see cref="ServerNodeSharing"/>). When it last did,
/// and whether that is recent enough to count, are judged by the database's clock alone, so servers whose clocks differ
/// agree on them. A single Server Node writes and reads nothing: it reports only itself.
/// </summary>
public sealed class ServerNodeBoard(IServiceScopeFactory scopes, DataDirectories directories)
{
    /// <summary>A server that has not written its part for this long no longer reports: it writes it every minute.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The file in a server's own data folder (<see cref="DataDirectories.Config"/>) that keeps its id: the id on the first
    /// line, and on the second the name of the machine it was made on.
    /// </summary>
    public const string IdFileName = "server-node-id.txt";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly Lock _making = new();
    private string? _node;

    /// <summary>
    /// This server's row: an id made the first time it is asked for, and kept in the server's own data folder
    /// (<see cref="IdFileName"/>) for its machine, so that a server that starts again, after a power cut too, writes over its
    /// own row. With an id for each run, it showed twice for a day, once as not reporting. A data folder copied to another
    /// machine, as when a machine is cloned after HC360 ran on it, makes an id of its own there: with one id, the two
    /// servers wrote one row, and each took the other's part for its own and left it out. Two HC360 servers on one machine,
    /// each with a data folder of its own, keep a row each; a machine renamed starts a new one.
    /// </summary>
    public string Node
    {
        get
        {
            lock (_making)
            {
                return _node ??= ReadOrMakeId(Path.Combine(directories.Config, IdFileName), Environment.MachineName);
            }
        }
    }

    /// <summary>
    /// Writes this server's part for the others, and removes the row of a server set up again in its place: the same
    /// machine and data folder, whose id was made anew (its id file lost with the folder, or the folder made again). A row
    /// of a machine renamed, or of a server set up in another folder, stays, not reporting, until someone removes it.
    /// </summary>
    public async Task PublishAsync(ServerNodeHealth part, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(part);
        var report = JsonSerializer.Serialize(part, Json);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO platform.server_nodes (node, report, reported_at)
            VALUES ({Node}, CAST({report} AS jsonb), now())
            ON CONFLICT (node) DO UPDATE SET report = EXCLUDED.report, reported_at = EXCLUDED.reported_at
            """, cancellationToken);
        await db.Database.ExecuteSqlAsync($"""
            DELETE FROM platform.server_nodes
            WHERE node <> {Node}
              AND report->'server'->>'machineName' = {part.Server.MachineName}
              AND report->'storage'->>'dataDirectory' = {part.Storage.DataDirectory}
            """, cancellationToken);
    }

    /// <summary>Removes this server's row, as it stops.</summary>
    public async Task WithdrawAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ServerNode>()
            .Where(n => n.Node == Node)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Every other server's part, as it last wrote it and when, and whether it still reports: one that stopped without
    /// saying so however long ago, until it is back or removed.
    /// </summary>
    public async Task<IReadOnlyList<ServerNodeHealth>> ReadOthersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.SqlQuery<ServerNodeRow>($"""
            SELECT report::text AS report, reported_at, reported_at > now() - {Lifetime} AS reporting
            FROM platform.server_nodes
            WHERE node <> {Node}
            """).ToListAsync(cancellationToken);
        return [.. rows.Select(Part).OfType<ServerNodeHealth>()];
    }

    /// <summary>The part of the server <paramref name="node"/>, read in <paramref name="db"/>'s transaction; null when it has none.</summary>
    internal static async Task<ServerNodeHealth?> ReadAsync(HospitalDbContext db, string node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var rows = await db.Database.SqlQuery<ServerNodeRow>($"""
            SELECT report::text AS report, reported_at, reported_at > now() - {Lifetime} AS reporting
            FROM platform.server_nodes
            WHERE node = {node}
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        return rows.Select(Part).OfType<ServerNodeHealth>().SingleOrDefault();
    }

    /// <summary>Removes the row of the server <paramref name="node"/>, in <paramref name="db"/>'s transaction.</summary>
    internal static Task<int> DeleteAsync(HospitalDbContext db, string node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Database.ExecuteSqlAsync($"DELETE FROM platform.server_nodes WHERE node = {node}", cancellationToken);
    }

    /// <summary>
    /// Whether a server that runs the background jobs (SCAL-05), other than this one, reports; null where the servers do
    /// not write their parts (without a shared folder, as on a single Server Node), so that it cannot be known.
    /// </summary>
    public async Task<bool?> JobsReportedAsync(CancellationToken cancellationToken) =>
        directories.IsShared ? (await ReadOthersAsync(cancellationToken)).Any(s => s.Reporting && SystemHealthService.RunsJobs(s)) : null;

    /// <summary>The id kept in <paramref name="path"/> for <paramref name="machine"/>, or a new one kept there for it from now on.</summary>
    private static string ReadOrMakeId(string path, string machine)
    {
        if (File.Exists(path)
            && File.ReadAllLines(path) is [var id, var madeOn, ..]
            && string.Equals(madeOn.Trim(), machine, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(id.Trim(), "N", out var kept))
        {
            return kept.ToString("N");
        }

        var made = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, [made, machine]);
        return made;
    }

    private static ServerNodeHealth? Part(ServerNodeRow row) =>
        JsonSerializer.Deserialize<ServerNodeHealth>(row.Report, Json) is { } part ? part with { ReportedAt = row.ReportedAt, Reporting = row.Reporting } : null;

    /// <summary>A row as read, its columns named as the model names them (snake case).</summary>
    private sealed record ServerNodeRow(string Report, DateTimeOffset ReportedAt, bool Reporting);
}

/// <summary>
/// Removes from System health a server of several that stopped reporting (SCAL-01), once it was retired, renamed, or set
/// up again elsewhere. Its row stays, and its alert is told every day, until it reports again or someone removes it: a
/// row's age alone says nothing of why it went silent. One that reports cannot be removed: stopped on purpose, it removes
/// itself. Whoever removes it acknowledges its alert, so nobody further is paged, and the alert then clears without a
/// word. The removal is in the audit trail. A server removed that reports again is shown again.
/// </summary>
public sealed class ServerRemoval(HospitalDbContext db, IAuditLogger audit, ICurrentUser user, TimeProvider time)
{
    public const string RemovedAction = "system.server_removed";

    /// <summary>Removes the server whose id is <paramref name="node"/> (<see cref="ServerInfo.Node"/>).</summary>
    public async Task<Result> RemoveAsync(string node, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // In turn with the alerts' checks, so one does not tell its alert again as it goes.
        await AlertIncidents.TakeTurnAsync(db, ct);
        if (await ServerNodeBoard.ReadAsync(db, node, ct) is not { } server)
        {
            return Error.NotFound("Server", node);
        }

        var name = server.Server.MachineName;
        if (server.Reporting)
        {
            return Error.Conflict("servers.reporting",
                $"{name} is reporting. Stop its HC360Server service, and it removes itself from System health.");
        }

        await ServerNodeBoard.DeleteAsync(db, node, ct);
        await AlertIncidents.AcknowledgeRemovedServerAsync(db, audit, user, time, name, ct);
        audit.Add(new AuditEntry(AuditCategories.Admin, RemovedAction, string.Create(CultureInfo.InvariantCulture,
            $"Removed {name} from System health: it had not reported since {server.ReportedAt:dd MMM yyyy HH:mm} UTC."))
        {
            EntityType = "Server",
            EntityId = name,
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }
}

/// <summary>
/// Writes this server's part of System health for the others (<see cref="ServerNodeBoard"/>) on a node of several, the node
/// that runs the jobs included: as it starts and every minute, and removes it as the server stops.
/// </summary>
[NodeLocal]
internal sealed class ServerNodeSharing(
    ServerNodeBoard board, IServiceScopeFactory scopes, DataDirectories directories, TimeProvider time, ILogger<ServerNodeSharing> logger)
    : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!directories.IsShared)
        {
            return;
        }

        var failing = false;
        using var timer = new PeriodicTimer(Every, time);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var part = await scope.ServiceProvider.GetRequiredService<SystemHealthService>().BuildServerAsync(stoppingToken);
                    await board.PublishAsync(part, stoppingToken);
                    failing = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Said once while it fails, not every minute.
                    if (!failing)
                    {
                        logger.LogWarning(ex, "This server's part of System health could not be shared with the other servers; trying again");
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
                // Its row shows it as no longer reporting, and goes after a day.
                logger.LogDebug(ex, "This server's part of System health was not removed as it stopped");
            }
        }
    }
}
