using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// Demonstration data only in a database made for it (audit P11). <see cref="Hosting.PlatformOptions.SeedDemoData"/> adds
/// demonstration patients, wards, stock, employees and staff accounts that share one configured password, and lets the
/// integrations use their simulators: turned on for a hospital's live database, it would mix all of that into real records,
/// and the accounts would open them. A database becomes one made for training or testing when a server with demonstration
/// data first starts on it while it holds no patients (a new database, or one installed as for a hospital with no one
/// registered yet, as docs/testing/LOAD-TESTS.md does), and stays one. On a database that holds patients and is not one, a
/// server with demonstration data does not start, and the load test adds no synthetic patients or accounts.
/// </summary>
public static class TrainingDatabase
{
    /// <summary>The setting that marks the database as made for training or testing. Only the server sets it.</summary>
    public const string SettingKey = "database.training";

    /// <summary>
    /// The mark's value, which only the server writes. Before HC360 kept the key, the settings API saved any key: a value
    /// saved under it then, even <c>false</c>, does not make a database one for training.
    /// </summary>
    internal const string MarkValue = "\"made for training or testing: no patients when marked\"";

    /// <summary>Why a server with demonstration data does not start on a database that holds a hospital's records.</summary>
    public const string LiveDatabaseMessage =
        "HealthCare360:SeedDemoData is on, but this database holds patients and was not made for demonstration data: it holds a " +
        "hospital's records. Demonstration data, its accounts and the simulators are only for a training or test server with a " +
        "database of its own, which had demonstration data on before anyone was registered. Turn SeedDemoData off in " +
        "config\\hc360.settings.json, or point this server at a training database, then start HC360 again.";

    /// <summary>Whether the database was made for training or testing.</summary>
    public static Task<bool> IsTrainingAsync(HospitalDbContext db, CancellationToken cancellationToken) =>
        db.Set<SystemSetting>().AnyAsync(s => s.Key == SettingKey && s.FacilityId == null && s.Value == MarkValue, cancellationToken);

    /// <summary>
    /// At start-up, before the seeders, with demonstration data on: marks a database that holds no patients as made for
    /// training, and stops start-up on one that holds patients and is not. One node at a time, as several may start at once.
    /// </summary>
    public static async Task CheckAsync(HospitalDbContext db, bool seedDemoData, CancellationToken cancellationToken)
    {
        if (!seedDemoData)
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({PlatformSql.AdvisoryLockKey})", cancellationToken);
        if (await IsTrainingAsync(db, cancellationToken))
        {
            return;
        }

        // The Patients module's table: every module's tables are in this one database.
        if (await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM patients.patients) AS \"Value\"").SingleAsync(cancellationToken))
        {
            throw new DatabaseUpgradeException(LiveDatabaseMessage);
        }

        // The key is unique, deleted rows too: a value saved under it before HC360 kept it gives way to the mark.
        var mark = await db.Set<SystemSetting>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.Key == SettingKey && s.FacilityId == null, cancellationToken);
        if (mark is null)
        {
            mark = new SystemSetting { Key = SettingKey };
            db.Add(mark);
        }

        mark.Value = MarkValue;
        mark.IsSecret = false;
        mark.IsDeleted = false;
        mark.DeletedAt = null;
        mark.DeletedBy = null;
        mark.Description = "Made for training or testing: a server with demonstration data (HealthCare360:SeedDemoData) may start on it.";
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Why the settings API does not change <see cref="SettingKey"/>: a database is made for training only as it starts.</summary>
    public static string? Problem(string key) =>
        string.Equals(key, SettingKey, StringComparison.OrdinalIgnoreCase)
            ? "HC360 sets this itself when a server with demonstration data first starts on a database that holds no patients."
            : null;
}
