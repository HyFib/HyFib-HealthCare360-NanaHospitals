using System.Globalization;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;

namespace HyFib.HealthCare360.Platform.Networking;

/// <summary>
/// The warm standby on System health beyond its slot (SRS DED-20): how far behind it is in time, as the primary measures it
/// (<see cref="StandbyStream.Lag"/>); that it cannot be measured, when HC360's account may not read it; and that a failover
/// replaced this server's database (<see cref="PrimaryFence"/>). The slot's own warnings, a standby that is away among
/// them, are <see cref="StandbyReplication.Warnings"/>.
/// </summary>
internal static class SystemHealthStandby
{
    /// <summary>A connected standby this far behind gets a warning: on the hospital network it keeps up within a second.</summary>
    public static readonly TimeSpan LagWarning = TimeSpan.FromMinutes(1);

    /// <summary>
    /// This far behind is critical: a failover now would lose more than the quarter of an hour the WAL archive allows
    /// (SRS REL-02), and the alerts tell IT on-call.
    /// </summary>
    public static readonly TimeSpan LagCritical = TimeSpan.FromMinutes(15);

    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(
        HospitalDbContext db, ReplicationState replication, DataDirectories directories, PlatformOptions options, CancellationToken ct)
    {
        var warnings = new List<HealthWarning>(Warnings(replication));
        try
        {
            if (await PrimaryFence.SupersededAsync(db, options, directories, ct) is { } superseded)
            {
                warnings.Add(new("replication.superseded", "critical",
                    $"{PrimaryFence.SupersededMessage(superseded.Record, superseded.Cluster)} This server stops within a minute."));
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            // The database's own warning says it cannot be asked.
        }

        return warnings;
    }

    /// <summary>
    /// For each connected standby: its lag, a warning from <see cref="LagWarning"/> and critical from <see cref="LagCritical"/>;
    /// and, where it cannot be measured, how to let HC360 measure it.
    /// </summary>
    internal static IReadOnlyList<HealthWarning> Warnings(ReplicationState state)
    {
        var warnings = new List<HealthWarning>();
        var unmeasured = new List<string>();
        foreach (var slot in state.Standbys.Where(s => s.Active))
        {
            var lag = state.Streams?.FirstOrDefault(s => s.Name == slot.Name)?.Lag;
            if (lag is not { } behind)
            {
                unmeasured.Add(slot.Name);
            }
            else if (behind >= LagWarning)
            {
                var critical = behind >= LagCritical;
                warnings.Add(new("replication.standby_lagging", critical ? "critical" : "warning",
                    $"The warm standby (replication slot {slot.Name}) is {Duration(behind)} behind: it has not applied the changes of the last {Duration(behind)}, " +
                    "which a failover now would lose. " +
                    (critical
                        ? "Check the network between the two servers, and the standby's disk and PostgreSQL log, now."
                        : "Check the network between the two servers, and that the standby is not short of disk or memory.")));
            }
        }

        if (unmeasured.Count > 0)
        {
            warnings.Add(new("replication.lag_unknown", "info",
                $"How far behind the warm standby ({string.Join(", ", unmeasured)}) is in time cannot be measured: HC360's database account may not read it. " +
                "Run Enable-HC360Replication.ps1 -StandbyAddress <the standby's address> again on this server, which lets it (after a restore, too)."));
        }

        return warnings;
    }

    private static string Duration(TimeSpan span) => span.TotalMinutes >= 120
        ? string.Create(CultureInfo.InvariantCulture, $"{span.TotalHours:0.#} hours")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(span.TotalMinutes):0} minutes");
}
