using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// PostgreSQL transaction-scoped advisory locks. Booking takes one lock per slot and one per patient before it
/// counts seats and checks overlaps, so two desks (or a desk and the portal) booking the same slot are serialised
/// across every API node, and the locks disappear automatically at commit or rollback. Keys are acquired in a fixed
/// order so two bookings can never deadlock on each other.
/// </summary>
internal static class AdvisoryLocks
{
    public static string SlotKey(Guid practitionerId, DateTimeOffset startsAt) => $"scheduling:slot:{practitionerId:N}:{startsAt.UtcTicks}";

    public static string PatientKey(Guid patientId) => $"scheduling:patient:{patientId:N}";

    /// <summary>Takes the locks inside the current transaction (call after BeginTransactionAsync).</summary>
    public static async Task AcquireAsync(HospitalDbContext db, IEnumerable<string> names, CancellationToken ct)
    {
        foreach (var key in names.Select(KeyOf).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        }
    }

    /// <summary>Stable 64-bit key derived from the lock name (identical on every server node).</summary>
    internal static long KeyOf(string name) => BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
}
