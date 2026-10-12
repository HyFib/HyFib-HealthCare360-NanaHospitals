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
public sealed record StandbySlot(string Name, bool Active, string? WalStatus, long? BehindBytes);

/// <summary>PostgreSQL's WAL archiver (<c>pg_stat_archiver</c>), when <c>archive_mode</c> is on (SRS REL-02).</summary>
public sealed record WalArchiverState(long ArchivedCount, DateTimeOffset? LastArchivedAt, long FailedCount, string? LastFailedWal, DateTimeOffset? LastFailedAt);

/// <summary>What System health shows of the warm standby and WAL archiving: no slots and no archiver when neither is set up.</summary>
public sealed record ReplicationState(IReadOnlyList<StandbySlot> Standbys, WalArchiverState? Archiver);

/// <summary>
/// Reads the warm standby's replication slot and the WAL archiver on the primary, and the warnings System health shows
/// for them. Everything it reads is open to every database account, so the server's own least-privilege account
/// (<c>hc360_app</c>) reads it: <c>pg_replication_slots</c>, <c>pg_stat_archiver</c>, <c>archive_mode</c> and the current
/// WAL position. How far behind the standby is in time, in <c>pg_stat_replication</c>, needs the
/// <c>pg_read_all_stats</c> role, which the account is not given: the bytes it still has to confirm stand in for it.
/// </summary>
public static class StandbyReplication
{
    /// <summary>The start of the names Enable-HC360Replication.ps1 gives a standby's slot.</summary>
    public const string SlotPrefix = "hc360_";

    /// <summary>A connected standby this far behind gets a warning: on the hospital network it keeps up within seconds.</summary>
    public const long BehindWarningBytes = 1L << 30;

    private const string SlotsSql = """
        SELECT slot_name, active, wal_status,
               CASE WHEN pg_is_in_recovery() OR restart_lsn IS NULL THEN NULL
                    ELSE pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)::bigint END
        FROM pg_replication_slots
        WHERE slot_type = 'physical' AND starts_with(slot_name, @prefix)
        ORDER BY slot_name
        """;

    private const string ArchiverSql = """
        SELECT archived_count, last_archived_time, failed_count, last_failed_wal, last_failed_time
        FROM pg_stat_archiver
        WHERE current_setting('archive_mode') <> 'off'
        """;

    /// <summary>Reads the slots and the archiver through <paramref name="connection"/>, which must be open.</summary>
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
                    reader.IsDBNull(3) ? null : reader.GetInt64(3)));
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

        return new ReplicationState(slots, archiver);
    }

    /// <summary>The System health warnings for the standby's slot and for WAL archiving.</summary>
    public static IReadOnlyList<HealthWarning> Warnings(ReplicationState state)
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
                warnings.Add(new("replication.standby_disconnected", "warning",
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
