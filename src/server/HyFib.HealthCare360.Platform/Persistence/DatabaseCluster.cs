using System.Data.Common;
using System.Globalization;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Which PostgreSQL cluster the hospital database lives in, and where its WAL is now, as the primary reports them to any
/// account (<c>pg_control_system()</c> and <c>pg_walfile_name(pg_current_wal_lsn())</c>). A warm standby is a copy of the
/// primary's cluster, so both share the system identifier; promoting the standby starts a new timeline, which the WAL
/// file's name begins with. Base backups and archived WAL belong to one system identifier (SRS REL-02), and a server
/// knows it was replaced by a promoted standby when the standby's timeline is newer than its own (SRS DED-20).
/// </summary>
/// <param name="SystemIdentifier">The cluster's system identifier, as text: it is a 64-bit number.</param>
/// <param name="WalFile">The name of the WAL file now written, such as <c>000000010000000000000042</c>.</param>
public sealed record DatabaseCluster(string SystemIdentifier, string WalFile)
{
    /// <summary>The timeline: the WAL file name's first eight hexadecimal digits.</summary>
    public int Timeline => TimelineOf(WalFile);

    /// <summary>The timeline a WAL file's name starts with (<c>00000002…</c> is timeline 2).</summary>
    public static int TimelineOf(string walFile) =>
        walFile.Length >= 8 && int.TryParse(walFile.AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var timeline)
            ? timeline
            : throw new FormatException($"'{walFile}' is not the name of a WAL file.");

    /// <summary>
    /// Reads the cluster through <paramref name="connection"/>, which must be open and on a primary: a standby has no WAL of
    /// its own to name (<c>pg_walfile_name</c> refuses during recovery).
    /// </summary>
    public static async Task<DatabaseCluster> ReadAsync(DbConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT system_identifier::text, pg_walfile_name(pg_current_wal_lsn()) FROM pg_control_system()";
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new DatabaseCluster(reader.GetString(0), reader.GetString(1))
            : throw new InvalidOperationException("PostgreSQL did not report its system identifier.");
    }
}
