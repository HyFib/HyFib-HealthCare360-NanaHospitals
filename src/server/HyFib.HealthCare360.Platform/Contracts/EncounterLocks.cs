using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// Keeps new work off a visit or a stay while it closes. Nothing is ordered on a visit or stay that has ended, and a
/// cancellation or a discharge withdraws the work not begun on it; both read the encounter and its work before they save,
/// so without a lock an order saved while the encounter closed would be missed by both. Placing an order and closing the
/// encounter therefore each take its transaction-scoped advisory lock before they read, and hold it to their commit: an
/// order placed first is there when the closing reads its work, and an order placed after reads the encounter closed.
/// </summary>
public static class EncounterLocks
{
    /// <summary>
    /// Takes the lock of each visit or stay given, in ascending key order so two callers never wait on each other. Call it
    /// in the transaction that orders on the encounter or closes it: the lock is released when that transaction ends.
    /// </summary>
    public static async Task LockAsync(HospitalDbContext db, IEnumerable<Guid?> encounterIds, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Lock a visit or a stay inside the transaction that orders on it or closes it: the lock lasts as long as that transaction.");
        }

        foreach (var key in encounterIds.OfType<Guid>().Select(Key).Distinct().Order())
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", ct);
        }
    }

    /// <summary>The lock key of a visit or a stay, the same on every server node.</summary>
    internal static long Key(Guid encounterId) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"encounter:{encounterId:N}")));
}
