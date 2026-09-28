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
}
