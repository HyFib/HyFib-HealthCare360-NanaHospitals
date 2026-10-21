using System.Globalization;
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

    /// <summary>
    /// Why start-up stops when an audit trail trigger must be put back (one was missing, turned off or changed) and the
    /// audit trail's lock is not granted within <see cref="PlatformSql.LockTimeout"/>. Trying again at once would hold every
    /// node's audit writes behind the lock again, while whatever holds the audit trail is still there.
    /// </summary>
    public const string AuditInUseMessage =
        "An audit trail trigger was missing, turned off or changed, and start-up could not put it back because the audit " +
        "trail is in use: it waited for the lock, gave up (\"canceling statement due to lock timeout\") and changed nothing. " +
        "Something still uses the audit trail, such as a long transaction writing to it, or a report or backup reading it " +
        "with a transaction open. Let it finish or stop it, then start HC360 again.";

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

        await ApplyPlatformSqlAsync(db, cancellationToken);

        foreach (var seeder in scope.ServiceProvider.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            logger.LogDebug("Running seeder {Seeder}", seeder.GetType().Name);
            await seeder.SeedAsync(db, cancellationToken);
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// <see cref="PlatformSql.PostMigrationScript"/> in a transaction of its own, one node at a time (several may start at
    /// once). A trigger it must make again waits at most <see cref="PlatformSql.LockTimeout"/> for its lock, so audit writes
    /// wait no longer behind it, and start-up then stops (<see cref="AuditInUseMessage"/>) rather than try again.
    /// </summary>
    internal static async Task ApplyPlatformSqlAsync(HospitalDbContext db, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({PlatformSql.AdvisoryLockKey})", ct);
            await db.Database.ExecuteSqlRawAsync(
                string.Create(CultureInfo.InvariantCulture, $"SET LOCAL lock_timeout = '{PlatformSql.LockTimeout.TotalMilliseconds:0}ms'"), ct);
            await db.Database.ExecuteSqlRawAsync(PlatformSql.PostMigrationScript, ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (IsLockTimeout(ex))
        {
            throw new DatabaseUpgradeException(AuditInUseMessage, ex);
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

/// <summary>
/// Database objects that EF migrations do not model: the audit trail's trigger functions and triggers. Every node runs it as
/// it starts (<see cref="DatabaseInitializer.ApplyPlatformSqlAsync"/>), an API node at any time beside the others (SCAL-01).
/// <para>
/// A trigger is made again only where it is not as it should be: missing, turned off, or changed. A node whose triggers are
/// as they should be locks nothing of the audit trail as it starts. Dropping and making the triggers again at every start
/// locked each monthly partition (SCAL-04) ACCESS EXCLUSIVE, newest first, until it had them all, so a report reading last
/// year's month held every node's audit writes behind a starting node until start-up gave up, a minute later, and tried
/// again. Making a trigger again locks its table SHARE ROW EXCLUSIVE, which readers do not wait for; only a changed
/// chain-link trigger, a constraint trigger that cannot be replaced, is dropped first (ACCESS EXCLUSIVE). That happens once
/// on a database whose chain-link trigger still has the WHEN clause it used to have.
/// </para>
/// </summary>
internal static class PlatformSql
{
    /// <summary>The advisory lock that has nodes starting at the same time put the triggers right one at a time.</summary>
    internal const long AdvisoryLockKey = 4_247_364;

    /// <summary>How long making a trigger again waits for its lock, and audit writes behind it; then start-up stops.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

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

        -- Each seal must link to the sealed event before it, and take a position no other event has (the table is
        -- partitioned by month, SCAL-04, so no unique index can say so). Checked at commit, so a batch sealed in one
        -- transaction may be written in any statement order. Only a seal is checked, which is every update the trigger
        -- above lets through. That is said here rather than in the trigger's WHEN clause: start-up reads each trigger's
        -- definition, below, and PostgreSQL locks the table to read a WHEN clause.
        CREATE OR REPLACE FUNCTION audit.check_audit_chain_link() RETURNS trigger AS $$
        BEGIN
            IF NEW.hash IS NULL THEN
                RETURN NULL;
            END IF;
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

        -- The triggers, each as pg_get_triggerdef prints the statement that makes it: one on every row for each function
        -- above (the chain-link one checked at commit), and one against TRUNCATE. Monthly partitions (SCAL-04) inherit the
        -- row triggers but not a statement trigger, so each partition has its own against TRUNCATE. A trigger is made
        -- again only where it is missing, turned off or changed. Reading them locks nothing (pg_inherits, not
        -- pg_partition_tree, which locks every partition), so while they are as they should be no table is locked.
        DO $triggers$
        DECLARE
            wanted record;
            found record;
            target regclass;
        BEGIN
            -- pg_get_triggerdef names a function in full only when it is not on the search path.
            PERFORM set_config('search_path', 'pg_catalog', true);
            FOR wanted IN
                WITH RECURSIVE tree (relid, depth) AS (
                    SELECT 'audit.audit_events'::regclass::oid, 0
                    UNION ALL
                    SELECT i.inhrelid, tree.depth + 1 FROM pg_inherits i JOIN tree ON i.inhparent = tree.relid)
                SELECT c.oid::regclass AS relation, format('%I.%I', n.nspname, c.relname) AS name, t.trigger_name,
                       t.definition, t.for_each_row AND tree.depth > 0 AS inherited
                FROM tree
                JOIN pg_class c ON c.oid = tree.relid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                CROSS JOIN (VALUES
                    (1, 'trg_protect_audit_events', true,
                     'CREATE TRIGGER trg_protect_audit_events BEFORE INSERT OR DELETE OR UPDATE ON %s '
                     'FOR EACH ROW EXECUTE FUNCTION audit.protect_audit_events()'),
                    (2, 'trg_check_audit_chain_link', true,
                     'CREATE CONSTRAINT TRIGGER trg_check_audit_chain_link AFTER UPDATE ON %s DEFERRABLE INITIALLY DEFERRED '
                     'FOR EACH ROW EXECUTE FUNCTION audit.check_audit_chain_link()'),
                    (3, 'trg_protect_audit_events_truncate', false,
                     'CREATE TRIGGER trg_protect_audit_events_truncate BEFORE TRUNCATE ON %s '
                     'FOR EACH STATEMENT EXECUTE FUNCTION audit.protect_audit_events()')
                ) AS t (position, trigger_name, for_each_row, definition)
                WHERE tree.depth = 0 OR c.relkind = 'r'
                ORDER BY tree.depth, c.relname, t.position
            LOOP
                SELECT pg_get_triggerdef(g.oid) AS definition, g.tgenabled AS enabled, g.tgconstraint <> 0 AS is_constraint
                  INTO found
                  FROM pg_trigger g
                 WHERE g.tgrelid = wanted.relation AND g.tgname = wanted.trigger_name;
                IF found.definition IS DISTINCT FROM format(wanted.definition, wanted.name) THEN
                    -- A partition's copy of a row trigger cannot be made alone: the table's is made again, with its copies.
                    target := CASE WHEN wanted.inherited THEN 'audit.audit_events'::regclass ELSE wanted.relation END;
                    IF wanted.definition LIKE 'CREATE CONSTRAINT %' OR found.is_constraint THEN
                        -- A constraint trigger cannot be replaced: it is dropped, which locks ACCESS EXCLUSIVE.
                        IF EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = target AND tgname = wanted.trigger_name) THEN
                            EXECUTE format('DROP TRIGGER %I ON %s', wanted.trigger_name, target);
                        END IF;
                        EXECUTE format(wanted.definition, target);
                    ELSE
                        EXECUTE format(replace(wanted.definition, 'CREATE TRIGGER', 'CREATE OR REPLACE TRIGGER'), target);
                    END IF;
                ELSIF found.enabled <> 'O' THEN
                    EXECUTE format('ALTER TABLE %s ENABLE TRIGGER %I', wanted.relation, wanted.trigger_name);
                END IF;
            END LOOP;
        END
        $triggers$;
        """;
}
