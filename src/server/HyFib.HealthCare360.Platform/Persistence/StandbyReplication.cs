using System.Data.Common;
using System.Globalization;
using HyFib.HealthCare360.Platform.Networking;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// The replication slot a warm standby Server Node streams through (SRS DED-20), as the primary's PostgreSQL has it.
/// Enable-HC360Replication.ps1 makes it, named <c>hc360_…</c>, and the primary keeps the WAL the standby still needs
/// while it is away, up to <c>max_slot_wal_keep_size</c>.
/// </summary>
/// <param name="Active">Whether the standby is connected and streaming now.</param>
/// <param name="WalStatus">PostgreSQL's <c>wal_status</c>: reserved or extended (the WAL is kept), unreserved (it is about
/// to be removed), lost (removed: the standby cannot catch up).</param>
/// <param name="BehindBytes">How much WAL the standby has not yet confirmed it wrote to disk: what a failover now would
/// lose, more or less.</param>
/// <param name="InactiveSince">Since when the standby has not been connected (PostgreSQL 17 records it); null while it is,
/// and on an older PostgreSQL.</param>
public sealed record StandbySlot(string Name, bool Active, string? WalStatus, long? BehindBytes, DateTimeOffset? InactiveSince = null);

/// <summary>
/// A connected standby as the primary measures it (<c>pg_stat_replication</c>), through the function
/// Enable-HC360Replication.ps1 makes for HC360 (<see cref="StandbyReplication.StatusFunction"/>).
/// </summary>
/// <param name="Name">Its <c>application_name</c>: the slot's name, as New-HC360Standby.ps1 sets it.</param>
/// <param name="ReplayLag">
/// How long after the primary wrote a change the standby had applied it, by the last change both saw: what a failover now
/// would lose, and more. Null when PostgreSQL has nothing to measure (an idle primary, after a while) or the function may
/// not read it.
/// </param>
/// <param name="ReplayBehindBytes">How much WAL the standby has still to apply.</param>
/// <param name="ReplyAt">When the standby last reported back.</param>
public sealed record StandbyStream(string Name, string? State, TimeSpan? ReplayLag, TimeSpan? FlushLag, long? ReplayBehindBytes, DateTimeOffset? ReplyAt)
{
    /// <summary>
    /// The standby's lag in time: <see cref="ReplayLag"/>, and none at all when PostgreSQL measures nothing because the
    /// standby has applied every byte; null when it cannot be told.
    /// </summary>
    public TimeSpan? Lag => ReplayLag ?? (ReplayBehindBytes == 0 ? TimeSpan.Zero : null);
}

/// <summary>PostgreSQL's WAL archiver (<c>pg_stat_archiver</c>), when <c>archive_mode</c> is on (SRS REL-02).</summary>
public sealed record WalArchiverState(long ArchivedCount, DateTimeOffset? LastArchivedAt, long FailedCount, string? LastFailedWal, DateTimeOffset? LastFailedAt);

/// <summary>What System health shows of the warm standby and WAL archiving: no slots and no archiver when neither is set up.</summary>
/// <param name="Streams">The connected standbys as the primary measures them; null when HC360 may not measure them (the
/// function Enable-HC360Replication.ps1 makes is not there).</param>
public sealed record ReplicationState(IReadOnlyList<StandbySlot> Standbys, WalArchiverState? Archiver, IReadOnlyList<StandbyStream>? Streams = null);

/// <summary>
/// Reads the warm standby's replication slot and the WAL archiver on the primary, and the warnings System health shows
/// for them. The slots, the archiver, <c>archive_mode</c> and the current WAL position are open to every database account,
/// so the server's own least-privilege account (<c>hc360_app</c>) reads them. How far behind the standby is in time, in
/// <c>pg_stat_replication</c>, needs the <c>pg_read_all_stats</c> role, which the account is not given: it reads it through
/// <see cref="StatusFunction"/>, which Enable-HC360Replication.ps1 makes as the superuser, and which shows the standbys'
/// lag and nothing else.
/// </summary>
public static class StandbyReplication
{
    /// <summary>The start of the names Enable-HC360Replication.ps1 gives a standby's slot.</summary>
    public const string SlotPrefix = "hc360_";

    /// <summary>The function that shows HC360's account the standbys' lag (Enable-HC360Replication.ps1 makes it).</summary>
    public const string StatusFunction = "public.hc360_replication_status()";

    /// <summary>A connected standby this far behind gets a warning: on the hospital network it keeps up within seconds.</summary>
    public const long BehindWarningBytes = 1L << 30;

    /// <summary>
    /// A standby away this long is critical: a failover now would lose everything since, more than the quarter of an hour
    /// the WAL archive allows (SRS REL-02).
    /// </summary>
    public static readonly TimeSpan AwayCriticalAfter = TimeSpan.FromMinutes(15);

    // inactive_since is PostgreSQL 17's: read through to_jsonb, so the query runs on an older one too.
    private const string SlotsSql = """
        SELECT slot_name, active, wal_status,
               CASE WHEN pg_is_in_recovery() OR restart_lsn IS NULL THEN NULL
                    ELSE pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)::bigint END,
               (to_jsonb(s) ->> 'inactive_since')::timestamptz
        FROM pg_replication_slots s
        WHERE slot_type = 'physical' AND starts_with(slot_name, @prefix)
        ORDER BY slot_name
        """;

    private const string ArchiverSql = """
        SELECT archived_count, last_archived_time, failed_count, last_failed_wal, last_failed_time
        FROM pg_stat_archiver
        WHERE current_setting('archive_mode') <> 'off'
        """;

    private const string StreamsSql = $"""
        SELECT application_name, state, replay_lag_seconds, flush_lag_seconds, replay_behind_bytes, reply_time
        FROM {StatusFunction}
        ORDER BY application_name
        """;

    /// <summary>Reads the slots, the archiver and the standbys' lag through <paramref name="connection"/>, which must be open.</summary>
    public static async Task<ReplicationState> ReadAsync(DbConnection connection, CancellationToken ct)
    {
        var slots = new List<StandbySlot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = SlotsSql;
            var prefix = command.CreateParameter();
            prefix.ParameterName = "prefix";
            prefix.Value = SlotPrefix;
            command.Parameters.Add(prefix);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                slots.Add(new StandbySlot(
                    reader.GetString(0),
                    reader.GetBoolean(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4)));
            }
        }

        WalArchiverState? archiver = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = ArchiverSql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                archiver = new WalArchiverState(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
            }
        }

        return new ReplicationState(slots, archiver, await ReadStreamsAsync(connection, ct));
    }

    /// <summary>The standbys as <see cref="StatusFunction"/> shows them; null when there is no such function.</summary>
    private static async Task<IReadOnlyList<StandbyStream>?> ReadStreamsAsync(DbConnection connection, CancellationToken ct)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = $"SELECT to_regprocedure('{StatusFunction}') IS NOT NULL";
            if (await exists.ExecuteScalarAsync(ct) is not true)
            {
                return null;
            }
        }

        var streams = new List<StandbyStream>();
        await using var command = connection.CreateCommand();
        command.CommandText = StreamsSql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            streams.Add(new StandbyStream(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : TimeSpan.FromSeconds(reader.GetDouble(2)),
                reader.IsDBNull(3) ? null : TimeSpan.FromSeconds(reader.GetDouble(3)),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        }

        return streams;
    }

    /// <summary>
    /// The System health warnings for the standby's slot and for WAL archiving. A standby away for
    /// <see cref="AwayCriticalAfter"/> or more, by <paramref name="now"/>, is critical.
    /// </summary>
    public static IReadOnlyList<HealthWarning> Warnings(ReplicationState state, DateTimeOffset? now = null)
    {
        var warnings = new List<HealthWarning>();
        foreach (var slot in state.Standbys)
        {
            if (slot.WalStatus == "lost")
            {
                warnings.Add(new("replication.standby_lost", "critical",
                    $"The warm standby (replication slot {slot.Name}) was away so long that this server removed WAL it still needed: it cannot catch up. " +
                    "Set it up again with New-HC360Standby.ps1."));
            }
            else if (slot.WalStatus == "unreserved")
            {
                warnings.Add(new("replication.standby_unreserved", "critical",
                    $"The warm standby (replication slot {slot.Name}) is so far behind that this server is about to remove WAL it still needs " +
                    "(max_slot_wal_keep_size). Reconnect it now, or it must be set up again."));
            }
            else if (!slot.Active)
            {
                var behind = slot.BehindBytes is > 0 ? $" and {Megabytes(slot.BehindBytes.Value)} behind" : string.Empty;
                var away = slot.InactiveSince is { } since && now is { } at && at - since >= AwayCriticalAfter;
                warnings.Add(away
                    ? new("replication.standby_disconnected", "critical",
                        $"The warm standby (replication slot {slot.Name}) has not been connected since {SystemHealthService.Since(slot.InactiveSince!.Value, now!.Value)}{behind}: " +
                        "a failover now would lose every change since. This server keeps the WAL it needs to catch up, up to max_slot_wal_keep_size: " +
                        "check that the standby is on, its PostgreSQL service running, and the network between them.")
                    : new("replication.standby_disconnected", "warning",
                        $"The warm standby (replication slot {slot.Name}) is not connected{behind}. This server keeps the WAL it needs to catch up, " +
                        "up to max_slot_wal_keep_size: check that the standby is on, and its PostgreSQL service running."));
            }
            else if (slot.BehindBytes > BehindWarningBytes)
            {
                warnings.Add(new("replication.standby_behind", "warning",
                    $"The warm standby (replication slot {slot.Name}) is {Megabytes(slot.BehindBytes.Value)} behind: a failover now would lose what it has not received. " +
                    "Check its network link."));
            }
        }

        if (state.Archiver is { FailedCount: > 0, LastFailedAt: { } failedAt } archiver && (archiver.LastArchivedAt is not { } archivedAt || failedAt > archivedAt))
        {
            warnings.Add(new("replication.archive_failing", "critical",
                $"WAL archiving is failing (last at {failedAt:dd MMM yyyy HH:mm} UTC, for {archiver.LastFailedWal}). PostgreSQL keeps every WAL file until it is archived, " +
                "so the disk fills, and point-in-time recovery lacks what was not archived. PostgreSQL's log says why: often the archive folder is not reachable or full."));
        }

        return warnings;
    }

    private static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB");
}
