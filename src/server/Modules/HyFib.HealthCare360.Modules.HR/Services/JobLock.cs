using Microsoft.EntityFrameworkCore.Storage;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Cluster-wide mutual exclusion for background jobs (SCAL-05: several worker nodes may run). Takes a PostgreSQL
/// transaction-scoped advisory lock; the lock is released when the returned transaction ends.
/// </summary>
internal static class JobLock
{
    public const long AttendanceComputation = 4_247_401;
    public const long CredentialExpiry = 4_247_402;

    /// <summary>Reading one attendance terminal: taken with the terminal as the second key (<see cref="TryAcquireAsync(HospitalDbContext, int, Guid, CancellationToken)"/>).</summary>
    public const int BiometricPull = 4_247_403;

    /// <summary>Opens a transaction and tries the lock; returns null (and rolls back) when another node holds it.</summary>
    public static async Task<IDbContextTransaction?> TryAcquireAsync(HospitalDbContext db, long key, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({key}) AS \"Value\"").SingleAsync(ct);
        if (acquired)
        {
            return transaction;
        }

        await transaction.DisposeAsync();
        return null;
    }

    /// <summary>The lock of one item of a kind of work, such as one terminal, so different items run side by side.</summary>
    public static async Task<IDbContextTransaction?> TryAcquireAsync(HospitalDbContext db, int kind, Guid item, CancellationToken ct)
    {
        var key = BitConverter.ToInt32(item.ToByteArray(), 0) ^ BitConverter.ToInt32(item.ToByteArray(), 12);
        var transaction = await db.Database.BeginTransactionAsync(ct);
        var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({kind}, {key}) AS \"Value\"").SingleAsync(ct);
        if (acquired)
        {
            return transaction;
        }

        await transaction.DisposeAsync();
        return null;
    }
}
