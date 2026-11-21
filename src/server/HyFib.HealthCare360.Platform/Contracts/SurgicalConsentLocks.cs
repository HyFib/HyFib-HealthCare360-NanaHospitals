using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Contracts;

/// <summary>
/// Keeps a patient's surgical consents still while an incision relies on them. The incision reads the patient's consents
/// to a high-risk procedure and to anaesthesia and keeps the ones it relied on; the front office records them, records a
/// refusal or withdraws one. Each takes the patient's transaction-scoped advisory lock before it reads or writes them and
/// holds it to its commit, so a refusal or a withdrawal recorded as the knife goes in is either read by the incision, which
/// is then refused, or recorded after it; and of two incisions on the same patient, the second reads the consent the first
/// relied on as taken.
/// </summary>
public static class SurgicalConsentLocks
{
    /// <summary>Takes the patient's lock. Call it in the transaction that reads or changes the consents: the lock lasts as long as that transaction.</summary>
    public static async Task LockAsync(HospitalDbContext db, Guid patientId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Lock a patient's surgical consents inside the transaction that reads or changes them: the lock lasts as long as that transaction.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({Key(patientId)})", ct);
    }

    /// <summary>The lock key of a patient's surgical consents, the same on every server node.</summary>
    internal static long Key(Guid patientId) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"surgical-consent:{patientId:N}")));
}
