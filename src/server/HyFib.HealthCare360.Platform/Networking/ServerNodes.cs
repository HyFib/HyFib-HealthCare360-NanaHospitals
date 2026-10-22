using System.Text.Json;
using System.Text.Json.Serialization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
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
/// removing it (a power cut) keeps its row, which shows that it no longer reports, until it starts again and writes over
/// it, or for a day.
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

    /// <summary>A row this old belongs to a server that stopped without removing it, and is no longer shown.</summary>
    private static readonly TimeSpan GoneAfter = TimeSpan.FromDays(1);

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

    /// <summary>Writes this server's part for the others, and removes the rows of servers gone for a day.</summary>
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
        await db.Database.ExecuteSqlAsync($"DELETE FROM platform.server_nodes WHERE reported_at < now() - {GoneAfter}", cancellationToken);
    }

    /// <summary>Removes this server's row, as it stops.</summary>
    public async Task WithdrawAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ServerNode>()
            .Where(n => n.Node == Node)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Every other server's part, as it last wrote it and when, and whether it still reports.</summary>
    public async Task<IReadOnlyList<ServerNodeHealth>> ReadOthersAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.SqlQuery<ServerNodeRow>($"""
            SELECT report::text AS report, reported_at, reported_at > now() - {Lifetime} AS reporting
            FROM platform.server_nodes
            WHERE node <> {Node} AND reported_at >= now() - {GoneAfter}
            """).ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(row => JsonSerializer.Deserialize<ServerNodeHealth>(row.Report, Json) is { } part
                ? part with { ReportedAt = row.ReportedAt, Reporting = row.Reporting }
                : null).OfType<ServerNodeHealth>(),
        ];
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

    /// <summary>A row as read, its columns named as the model names them (snake case).</summary>
    private sealed record ServerNodeRow(string Report, DateTimeOffset ReportedAt, bool Reporting);
}

/// <summary>
/// Writes this server's part of System health for the others (<see cref="ServerNodeBoard"/>) on a node of several, the node
/// that runs the jobs included: as it starts and every minute, and removes it as the server stops.
/// </summary>
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
