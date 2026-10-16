using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

        var backups = services.GetRequiredService<BackupService>();
        BackupResult? backup = null;
        if ((applied.Count > 0 || createdBeforeMigrations) && options.Value.Backup.BeforeMigrations)
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

            logger.LogInformation("Backup {File} taken and verified before the update", backup.FileName);
        }

        if (adoption is not null)
        {
            await adoption.ApplyAsync(db, logger, ct);
            pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(ct);
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
}

/// <summary>Database objects that EF migrations do not model (idempotent; runs after every migration).</summary>
internal static class PlatformSql
{
    public const string PostMigrationScript = """
        -- Audit events are append-only: forbid DELETE/TRUNCATE, rows inserted already sealed, and any change except
        -- sealing the hash chain. Sealing is all or nothing: the hash, the chain position and the link to the previous
        -- hash together (only the first event has no previous hash), so no event can drop out of verification.
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
               OR (NEW.id, NEW.occurred_at, NEW.category, NEW.action, NEW.entity_type, NEW.entity_id, NEW.patient_id,
                   NEW.user_id, NEW.facility_id, NEW.summary, NEW.details)
                  IS DISTINCT FROM
                  (OLD.id, OLD.occurred_at, OLD.category, OLD.action, OLD.entity_type, OLD.entity_id, OLD.patient_id,
                   OLD.user_id, OLD.facility_id, OLD.summary, OLD.details) THEN
                RAISE EXCEPTION 'audit events can only be sealed completely, not edited';
            END IF;
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_protect_audit_events ON audit.audit_events;
        CREATE TRIGGER trg_protect_audit_events
            BEFORE INSERT OR UPDATE OR DELETE ON audit.audit_events
            FOR EACH ROW EXECUTE FUNCTION audit.protect_audit_events();

        -- Each seal must link to the sealed event before it. Checked at commit, so a batch sealed in one transaction
        -- may be written in any statement order.
        CREATE OR REPLACE FUNCTION audit.check_audit_chain_link() RETURNS trigger AS $$
        BEGIN
            IF NEW.chain_index > 1 AND NOT EXISTS (
                SELECT 1 FROM audit.audit_events p
                WHERE p.chain_index = NEW.chain_index - 1 AND p.hash = NEW.previous_hash) THEN
                RAISE EXCEPTION 'audit events must be sealed in chain order';
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
        """;
}
