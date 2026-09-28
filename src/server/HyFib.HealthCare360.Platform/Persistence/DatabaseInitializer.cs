using HyFib.HealthCare360.Platform.Hosting;
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
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();

        if (options.Value.AutoMigrate)
        {
            if (db.Database.GetMigrations().Any())
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
                    await db.Database.MigrateAsync(cancellationToken);
                }
            }
            else
            {
                // Development fallback before the first migration exists.
                await db.Database.EnsureCreatedAsync(cancellationToken);
            }
        }

        await db.Database.ExecuteSqlRawAsync(PlatformSql.PostMigrationScript, cancellationToken);

        foreach (var seeder in scope.ServiceProvider.GetServices<ISeeder>().OrderBy(s => s.Order))
        {
            logger.LogDebug("Running seeder {Seeder}", seeder.GetType().Name);
            await seeder.SeedAsync(db, cancellationToken);
            db.ChangeTracker.Clear();
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
