using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Idempotent reference/default data provider. Seeders run in ascending <see cref="Order"/> after
/// migrations on every start, so they must check before inserting.
/// </summary>
public interface ISeeder
{
    /// <summary>0–99 platform, 100+ modules.</summary>
    int Order { get; }

    Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken);
}

/// <summary>Brings the database schema up to date and runs seeders.</summary>
public sealed class DatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    ILogger<DatabaseInitializer> logger)
{
    /// <summary>Why HC360 does not start on a database that is a warm standby (SRS DED-20).</summary>
    public const string StandbyMessage =
        "The database is a read-only warm standby: PostgreSQL is in recovery, following the primary Server Node. HC360 runs only " +
        "against the primary. To fail over to this Server Node, promote its PostgreSQL first, then put the keys back and start HC360 " +
        "as the failover steps say (docs/deployment/DEPLOYMENT.md, \"Failing over to the standby\").";

    /// <summary>
    /// Why start-up stops when a migration cannot lock a table it changes: it waited for the lock as long as it allows, then
    /// gave up (SQLSTATE 55P03) and rolled back. Trying again would wait for the lock again, and hold every write to the
    /// table behind it meanwhile, while whatever holds it is still there.
    /// </summary>
    public const string LockedMessage =
        "The database update stopped because a table it changes is in use: it waited for the table's lock, gave up " +
        "(\"canceling statement due to lock timeout\") and changed nothing. Something still uses the database, such as " +
        "another HC360 server left running or a reporting tool with a transaction open. Stop it, then start HC360 again.";

    /// <summary>No limit (0) on a migration's statements: see where the migrations are applied.</summary>
    internal const int MigrationCommandTimeout = 0;

    // The backup taken before this process first tried the pending migrations. Start-up tries the update again when the
    // database goes away during it, and the same update needs no second backup: each is a full pg_dump of the database.
    private PreMigrationBackup? _backupTaken;

    /// <summary>A migration gave up waiting for a lock (lock_timeout), however EF Core wrapped the error.</summary>
    public static bool IsLockTimeout(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable })
            {
                return true;
            }
        }

        return false;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

        // Before anything is written: on a standby every write fails, and the service would restart into the same failure.
        if (await db.Database.SqlQuery<bool>($"SELECT pg_is_in_recovery() AS \"Value\"").SingleAsync(cancellationToken))
        {
            throw new DatabaseUpgradeException(StandbyMessage);
        }

        if (options.Value.AutoMigrate)
        {
            await MigrateAsync(scope.ServiceProvider, db, cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync(PlatformSql.PostMigrationScript, cancellationToken);

        foreach (var seeder in scope.ServiceProvider.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            logger.LogDebug("Running seeder {Seeder}", seeder.GetType().Name);
            await seeder.SeedAsync(db, cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// Applies pending migrations (MNT-03). A database that already holds data is backed up first and verified, and it is
    /// not changed at all when that backup fails. A database created before migrations existed is adopted
    /// (<see cref="MigrationBaseline"/>) rather than having every table created again; that is planned before the backup,
    /// so a database that cannot be adopted stops start-up without one. A new, empty database is simply migrated.
    /// </summary>
    private async Task MigrateAsync(IServiceProvider services, HospitalDbContext db, CancellationToken ct)
    {
        var applied = await MigrationBaseline.HistoryExistsAsync(db, ct) ? (await db.Database.GetAppliedMigrationsAsync(ct)).ToList() : [];
        var pending = db.Database.GetMigrations().Except(applied).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var createdBeforeMigrations = applied.Count == 0 && await MigrationBaseline.HasApplicationTablesAsync(db, ct);
        var adoption = createdBeforeMigrations ? await MigrationBaseline.PrepareAsync(db, ct) : null;

        var backups = services.GetRequiredService<IPreMigrationBackup>();
        BackupResult? backup = null;
        if ((applied.Count > 0 || createdBeforeMigrations) && options.Value.Backup.BeforeMigrations)
        {
            // An earlier attempt at this update (all of what is pending now was pending then) took its backup already.
            if (_backupTaken is { } taken && pending.All(taken.Pending.Contains))
            {
                backup = taken.Backup;
                logger.LogInformation("Backup {File} was taken before the first attempt at this update; no other is taken", backup.FileName);
            }
            else
            {
                logger.LogInformation("Backing up the database before applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                try
                {
                    backup = await backups.CreatePreMigrationAsync(applied, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new DatabaseUpgradeException(
                        $"The backup before the database update failed, so the database was not changed: {ex.Message} " +
                        "Fix the cause (pg_dump, disk space) and start HC360 again. To update without a backup, set " +
                        "HealthCare360:Backup:BeforeMigrations to false (not recommended).",
                        ex);
                }

                _backupTaken = new PreMigrationBackup(pending.ToHashSet(StringComparer.Ordinal), backup);
                logger.LogInformation("Backup {File} taken and verified before the update", backup.FileName);
            }
        }

        if (adoption is not null)
        {
            await adoption.ApplyAsync(db, logger, ct);
            pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));

            // A migration that rebuilds a large table (the monthly partitions of the audit trail, SCAL-04) may take many
            // minutes. It runs in a transaction, during the upgrade, so it gets the time it needs, not a request's minute.
            var timeout = db.Database.GetCommandTimeout();
            db.Database.SetCommandTimeout(MigrationCommandTimeout);
            try
            {
                await db.Database.MigrateAsync(ct);
            }
            catch (Exception ex) when (IsLockTimeout(ex))
            {
                // EF Core wraps PostgreSQL's error in an InvalidOperationException that Npgsql calls transient; start-up
                // must not try again (see LockedMessage).
                throw new DatabaseUpgradeException(LockedMessage, ex);
            }
            finally
            {
                db.Database.SetCommandTimeout(timeout);
            }
        }

        if (backup is not null)
        {
            // The database is updated and the backup file is safe on disk; not listing it must not stop HC360 starting.
            try
            {
                await backups.RecordAsync(backup, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The backup {File} taken before the update could not be added to the backup history", backup.FileName);
            }
        }
    }

    /// <param name="Pending">The migrations that were pending when it was taken.</param>
    private sealed record PreMigrationBackup(IReadOnlySet<string> Pending, BackupResult Backup);
}

/// <summary>Database objects that EF migrations do not model (idempotent; runs after every migration).</summary>
internal static class PlatformSql
{
    public const string PostMigrationScript = """
        -- Audit events are append-only: forbid DELETE/TRUNCATE, rows inserted already sealed, and any change except
        -- sealing the hash chain. Sealing is all or nothing: the hash, the chain position and the link to the previous
        -- hash together (only the first event has no previous hash), so no event can drop out of verification. It
        -- changes nothing else in the row, a column added later included.
        CREATE OR REPLACE FUNCTION audit.protect_audit_events() RETURNS trigger AS $$
        BEGIN
            IF TG_OP = 'DELETE' OR TG_OP = 'TRUNCATE' THEN
                RAISE EXCEPTION 'audit events are append-only';
            END IF;
            IF TG_OP = 'INSERT' THEN
                IF NEW.hash IS NOT NULL OR NEW.chain_index IS NOT NULL OR NEW.previous_hash IS NOT NULL THEN
                    RAISE EXCEPTION 'audit events are sealed by the server, never inserted sealed';
                END IF;
                RETURN NEW;
            END IF;
            IF OLD.hash IS NOT NULL THEN
                RAISE EXCEPTION 'sealed audit events cannot be modified';
            END IF;
            IF NEW.hash IS NULL
               OR NEW.chain_index IS NULL
               OR NEW.chain_index < 1
               OR (NEW.chain_index = 1) <> (NEW.previous_hash IS NULL)
               OR (to_jsonb(NEW) - 'hash' - 'chain_index' - 'previous_hash')
                  IS DISTINCT FROM
                  (to_jsonb(OLD) - 'hash' - 'chain_index' - 'previous_hash') THEN
                RAISE EXCEPTION 'audit events can only be sealed completely, not edited';
            END IF;
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_protect_audit_events ON audit.audit_events;
        CREATE TRIGGER trg_protect_audit_events
            BEFORE INSERT OR UPDATE OR DELETE ON audit.audit_events
            FOR EACH ROW EXECUTE FUNCTION audit.protect_audit_events();

        -- Each seal must link to the sealed event before it, and take a position no other event has (the table is
        -- partitioned by month, SCAL-04, so no unique index can say so). Checked at commit, so a batch sealed in one
        -- transaction may be written in any statement order.
        CREATE OR REPLACE FUNCTION audit.check_audit_chain_link() RETURNS trigger AS $$
        BEGIN
            IF NEW.chain_index > 1 AND NOT EXISTS (
                SELECT 1 FROM audit.audit_events p
                WHERE p.chain_index = NEW.chain_index - 1 AND p.hash = NEW.previous_hash) THEN
                RAISE EXCEPTION 'audit events must be sealed in chain order';
            END IF;
            IF EXISTS (SELECT 1 FROM audit.audit_events d WHERE d.chain_index = NEW.chain_index AND d.id <> NEW.id) THEN
                RAISE EXCEPTION 'audit chain position % is already taken', NEW.chain_index;
            END IF;
            RETURN NULL;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_check_audit_chain_link ON audit.audit_events;
        CREATE CONSTRAINT TRIGGER trg_check_audit_chain_link
            AFTER UPDATE ON audit.audit_events
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW WHEN (NEW.hash IS NOT NULL)
            EXECUTE FUNCTION audit.check_audit_chain_link();

        DROP TRIGGER IF EXISTS trg_protect_audit_events_truncate ON audit.audit_events;
        CREATE TRIGGER trg_protect_audit_events_truncate
            BEFORE TRUNCATE ON audit.audit_events
            FOR EACH STATEMENT EXECUTE FUNCTION audit.protect_audit_events();

        -- Monthly partitions (SCAL-04) inherit the row triggers above, but not a statement trigger: each gets its own, so
        -- TRUNCATE on a partition is refused too.
        DO $$
        DECLARE
            partition regclass;
        BEGIN
            FOR partition IN SELECT relid FROM pg_partition_tree('audit.audit_events') WHERE isleaf AND relid <> 'audit.audit_events'::regclass LOOP
                EXECUTE format('DROP TRIGGER IF EXISTS trg_protect_audit_events_truncate ON %s', partition);
                EXECUTE format('CREATE TRIGGER trg_protect_audit_events_truncate BEFORE TRUNCATE ON %s FOR EACH STATEMENT EXECUTE FUNCTION audit.protect_audit_events()', partition);
            END LOOP;
        END;
        $$;
        """;
}
