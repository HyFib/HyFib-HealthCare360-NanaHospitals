namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// Serialises concurrent scheduling of the same theatre, surgeon, anaesthetist or patient with PostgreSQL
/// transaction-scoped advisory locks. Without it two coordinators could both pass the overlap check and commit a
/// double booking; time ranges cannot be protected by a plain unique index. Locks are released automatically at
/// commit or rollback, so this must run inside <c>BeginTransactionAsync</c>.
/// </summary>
internal static class SchedulingLocks
{
    /// <summary>First key of the two-key advisory lock: a namespace so OT locks never collide with other modules.</summary>
    private const int OtNamespace = 0x4F54_0001;

    public static async Task AcquireAsync(HospitalDbContext db, IEnumerable<Guid> resources, CancellationToken ct)
    {
        // Always lock in ascending key order so two transactions locking the same resources cannot deadlock.
        foreach (var key in resources.Where(id => id != Guid.Empty).Select(Key).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({OtNamespace}, {key})", ct);
        }
    }

    /// <summary>Folds a GUID into the 32-bit lock key; collisions only cause harmless extra waiting.</summary>
    internal static int Key(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes);
        return BitConverter.ToInt32(bytes) ^ BitConverter.ToInt32(bytes[4..]) ^ BitConverter.ToInt32(bytes[8..]) ^ BitConverter.ToInt32(bytes[12..]);
    }
}
