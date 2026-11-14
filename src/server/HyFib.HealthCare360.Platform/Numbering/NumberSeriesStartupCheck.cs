using System.Globalization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>What start-up did with the high-water mark: the mark it compared with (null when none was found), the series it raised, and the restore it acted on.</summary>
public sealed record NumberSeriesStartupOutcome(NumberSeriesMarks? Mark, IReadOnlyList<NumberSeriesRaise> Raised, RestoreMarkerContent? Restore);

/// <summary>Whether this server's start-up could compare the number series with their high-water mark: the reason when it could not.</summary>
public sealed class NumberSeriesStartupStatus
{
    public string? Failure { get; set; }
}

/// <summary>
/// At every start, as the database is brought up to date and before the server takes requests (a seeder, before every
/// other): each number series the database has behind the high-water mark (<see cref="NumberSeriesHighWaterMark"/>) is
/// moved past it by <see cref="NumberSeriesHighWater.Margin"/>, with an audit entry for each (<see cref="RaisedAction"/>), so
/// that the numbers issued after the backup a restored database came from (on cards, wristbands and GST invoices) are not
/// issued again. A series the database lost (its first number came after the backup) is made again likewise. So a restore
/// made by hand is caught as well as one made by Restore-HC360.ps1, and a period moved on (a new year's numbers) is kept.
/// <para>
/// After Restore-HC360.ps1, whose marker (<see cref="RestoreMarker"/>) is in the data directory, it also holds outgoing
/// messages (<see cref="OutboxHoldSetting"/>): messages queued in the backup may have been sent since, and would go again.
/// System health then says so, critically, until they are released (<see cref="RestoreHealthWarnings"/>). The raises, the
/// hold and the record of the restore are saved in one transaction, then the marker is renamed: it is acted on once. If
/// any of that fails after a restore, start-up stops rather than issue numbers or send messages twice; otherwise the
/// failure is logged and System health says so.
/// </para>
/// </summary>
internal sealed class NumberSeriesStartupCheck(
    NumberSeriesHighWaterMark highWater,
    RestoreMarker marker,
    NumberSeriesStartupStatus status,
    NumberSeriesCatalog catalog,
    ISettingsService settings,
    IAuditLogger audit,
    TimeProvider time,
    ILogger<NumberSeriesStartupCheck> logger) : ISeeder
{
    /// <summary>
    /// The hospital-wide setting that holds outgoing messages (the outbox's internet-bound messages): an object with
    /// <c>reason</c>, <c>since</c> (UTC) and <c>backupTakenAt</c> (UTC, or null). The outbox reads it, and releasing the
    /// messages removes it.
    /// </summary>
    public const string OutboxHoldSetting = "messaging.outbox.hold";

    /// <summary>The <c>reason</c> of a hold made after a restore.</summary>
    public const string RestoreHoldReason = "restore";

    /// <summary>The hospital-wide setting that records the restore the server last started after (<see cref="ServerRestore"/>).</summary>
    public const string RestoreSetting = "platform.restore.last";

    /// <summary>The audit action of a series raised past the high-water mark.</summary>
    public const string RaisedAction = "numbering.raised";

    /// <summary>Tries before start-up gives up on series that keep changing under it (another server issuing numbers on this database).</summary>
    private const int Attempts = 5;

    /// <summary>First: before any seeder could issue a number.</summary>
    public int Order => 1;

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var restore = marker.Read();
        try
        {
            // A start that saved it all but could not rename the marker: the database records that restore already.
            if (restore is not null && await RecordedAsync(settings, cancellationToken) is { } recorded
                && recorded.RestoredAt == restore.RestoredAt && string.Equals(recorded.BackupFile, restore.BackupFile, StringComparison.Ordinal))
            {
                marker.Consume();
                restore = null;
            }

            await CheckAsync(db, restore, cancellationToken);
            status.Failure = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsTransient(ex))
        {
            status.Failure = SensitiveText.Redact(ex.Message);
            if (restore is not null)
            {
                throw new DatabaseUpgradeException(
                    $"HC360 was restored from a backup, and start-up could not move the number series past the numbers issued before it, or hold the outgoing messages ({ex.Message}). It stops rather than issue those numbers or send those messages again. Fix the cause and start HC360 again: the restore marker {marker.Path} is kept until then.",
                    ex);
            }

            logger.LogCritical(ex, "The number series could not be compared with their high-water mark; HC360 starts all the same, and System health says so");
        }
    }

    /// <summary>
    /// Compares the series with the newest high-water mark of this database and raises those behind it; with
    /// <paramref name="restore"/> (the marker's content), also holds outgoing messages, records the restore, and renames the
    /// marker.
    /// </summary>
    internal async Task<NumberSeriesStartupOutcome> CheckAsync(HospitalDbContext db, RestoreMarkerContent? restore, CancellationToken cancellationToken)
    {
        var mark = await NumberSeriesHighWater.DatabaseIdAsync(db, cancellationToken) is { } database ? highWater.ReadNewest(database) : null;
        if (mark is null && restore is null)
        {
            return new NumberSeriesStartupOutcome(null, [], null);
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var rows = await db.Set<NumberSeries>().IgnoreQueryFilters().ToListAsync(cancellationToken);
                var raises = mark is null
                    ? []
                    : NumberSeriesHighWater.Raises([.. rows.Select(Counter)], mark.Series, NumberFormat.ToHospitalTime(now));
                var raised = await RaiseAsync(db, rows, raises, mark, restore, now, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                if (restore is not null)
                {
                    await settings.SetAsync(OutboxHoldSetting, new { reason = RestoreHoldReason, since = now, backupTakenAt = restore.BackupTakenAt?.ToUniversalTime() },
                        cancellationToken: cancellationToken);
                    await settings.SetAsync(RestoreSetting, new ServerRestore(restore.RestoredAt, restore.BackupFile, restore.BackupTakenAt, now, mark?.WrittenAt, raised),
                        cancellationToken: cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                Report(mark, raises, raised, restore);
                if (restore is not null)
                {
                    marker.Consume();
                }

                return new NumberSeriesStartupOutcome(mark, raises, restore);
            }
            catch (DbUpdateException ex) when (attempt < Attempts
                                               && (ex is DbUpdateConcurrencyException || ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }))
            {
                // A number was issued meanwhile, by another server on this database: read the series again.
                logger.LogInformation("A number series changed while start-up raised it; reading the series again");
            }
        }
    }

    internal static NumberSeriesMark Counter(NumberSeries row) =>
        new(row.FacilityId, row.Key, row.CurrentPeriod, row.CurrentValue, row.ResetPolicy, row.Format);

    /// <summary>The restore the database records (<see cref="RestoreSetting"/>); null when none, or when it was changed into something else by hand.</summary>
    internal static async Task<ServerRestore?> RecordedAsync(ISettingsService settings, CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAsync<ServerRestore>(RestoreSetting, null, cancellationToken);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Applies <paramref name="raises"/> to the series read (and makes again those the database lost), audits each, and names them for System health.</summary>
    private async Task<IReadOnlyList<RaisedNumberSeries>> RaiseAsync(
        HospitalDbContext db, List<NumberSeries> rows, IReadOnlyList<NumberSeriesRaise> raises, NumberSeriesMarks? mark, RestoreMarkerContent? restore,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (raises.Count == 0 || mark is null)
        {
            return [];
        }

        var facilities = await db.Set<Facility>().IgnoreQueryFilters().AsNoTracking()
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id)
            .Select(f => new { f.Id, f.Code })
            .ToListAsync(cancellationToken);
        var mainCode = facilities.FirstOrDefault()?.Code;
        var localNow = NumberFormat.ToHospitalTime(now);
        var raised = new List<RaisedNumberSeries>();
        foreach (var raise in raises)
        {
            var key = raise.Mark.Key;
            var facilityId = raise.Mark.FacilityId;
            var row = rows.FirstOrDefault(r => r.Key == key && r.FacilityId == facilityId);
            if (row is null)
            {
                row = new NumberSeries { Key = key, FacilityId = facilityId, Format = raise.Mark.Format, ResetPolicy = raise.Mark.ResetPolicy };
                db.Add(row);
            }

            row.CurrentValue = raise.Value;
            row.CurrentPeriod = raise.Period;

            var definition = catalog.Find(key);
            var format = definition is null ? row.Format : NumberSeriesService.WithFacilityCode(definition, row.Format);
            var code = facilityId is { } id ? facilities.FirstOrDefault(f => f.Id == id)?.Code : mainCode;
            var next = NumberFormat.Render(format, raise.Value + 1, code, localNow);
            var before = raise.Before is { } counter
                ? NumberFormat.Render(format, NumberFormat.LastIssued(counter.Value, counter.Period, counter.ResetPolicy, localNow) + 1, code, localNow)
                : null;
            var name = definition?.Name ?? key;
            var series = new RaisedNumberSeries(key, name, facilityId is null ? null : code, next, before);
            raised.Add(series);

            audit.Add(new AuditEntry(AuditCategories.Admin, RaisedAction, string.Create(CultureInfo.InvariantCulture,
                $"{name}{(series.FacilityCode is { } at ? " at " + at : string.Empty)} numbers moved past the last number issued before the database was restored: next {next}{(before is null ? " (the series was made again)" : $", not {before}")}"))
            {
                EntityType = nameof(NumberSeries),
                EntityId = key,
                Severity = AuditSeverity.Warning,
                Details = new
                {
                    series = key,
                    facilityId,
                    before = raise.Before is { } b ? new { period = b.Period, value = b.Value } : null,
                    mark = new { period = raise.Mark.Period, value = raise.Mark.Value, writtenAt = mark.WrittenAt },
                    after = new { period = raise.Period, value = raise.Value },
                    margin = NumberSeriesHighWater.Margin,
                    restoredFrom = restore?.BackupFile,
                },
            });
        }

        return raised;
    }

    private void Report(NumberSeriesMarks? mark, IReadOnlyList<NumberSeriesRaise> raises, IReadOnlyList<RaisedNumberSeries> raised, RestoreMarkerContent? restore)
    {
        if (raises.Count > 0)
        {
            logger.LogWarning(
                "{Count} number series were behind their high-water mark of {WrittenAt:u} (the database was restored, or put back) and were moved past it: {Series}",
                raises.Count, mark!.WrittenAt, string.Join("; ", raised.Select(r => $"{r.Name}{(r.FacilityCode is { } code ? " at " + code : string.Empty)} next {r.NextNumber}")));
        }

        if (restore is not null)
        {
            logger.LogWarning(
                "HC360 starts on a database restored {RestoredAt:u} from {Backup}: outgoing messages are held until an administrator checks and releases them{Mark}",
                restore.RestoredAt, restore.BackupFile ?? "a backup",
                mark is null ? "; no high-water mark of the number series was found, so none was raised" : string.Empty);
        }
    }

    /// <summary>A failure start-up tries again in a moment (<c>ServerComposition.IsTransientDatabaseError</c>): PostgreSQL not ready yet.</summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        TimeoutException => true,
        NpgsqlException npgsql => npgsql.IsTransient,
        InvalidOperationException { InnerException: NpgsqlException inner } => inner.IsTransient,
        DbUpdateException { InnerException: NpgsqlException inner } => inner.IsTransient,
        _ => false,
    };
}

/// <summary>Registers the number series' high-water mark and what start-up does with it (<see cref="NumberSeriesStartupCheck"/>, a seeder found by the module scan).</summary>
public static class NumberSeriesHighWaterServiceCollectionExtensions
{
    /// <summary>
    /// The mark, its job (which writes it only on a node that runs the jobs), the restore marker, and System health's
    /// warning after a restore.
    /// </summary>
    public static IServiceCollection AddNumberSeriesHighWaterMark(this IServiceCollection services)
    {
        services.AddSingleton<NumberSeriesHighWaterMark>();
        services.AddSingleton<RestoreMarker>();
        services.AddSingleton<NumberSeriesStartupStatus>();
        services.AddHostedService<NumberSeriesHighWaterJob>();
        services.AddScoped<IHealthWarningSource, RestoreHealthWarnings>();
        return services;
    }
}
