using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;

namespace HyFib.HealthCare360.Platform.Auditing;

/// <summary>
/// The key that seals the audit chain (<c>keys/audit-seal.key</c>): 32 random bytes wrapped by Data Protection like the
/// field master key, so it is held outside the database. Each sealed audit event carries an HMAC-SHA256 of its chain hash
/// under this key (<see cref="AuditEvent.Mac"/>). The SHA-256 chain alone shows a changed row to anyone, but whoever can
/// write the database can also compute a new chain over changed rows; without this key they cannot compute the seals.
/// Backups escrow it beside the field keys, so a restored server can still verify the seals it made.
/// </summary>
public sealed class AuditSealKey
{
    public const string FileName = "audit-seal.key";
    private const string ProtectorPurpose = "HC360.Audit.SealKey.v1";

    private readonly Lazy<byte[]> _key;

    public AuditSealKey(DataDirectories directories, IDataProtectionProvider dataProtection)
    {
        var path = PathIn(directories);
        var protector = CreateProtector(dataProtection);
        _key = new Lazy<byte[]>(() => FieldMasterKey.LoadOrCreate(path, protector), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Keys, FileName);

    public static IDataProtector CreateProtector(IDataProtectionProvider dataProtection) => dataProtection.CreateProtector(ProtectorPurpose);

    /// <summary>A short, non-secret identifier of the key, which backups record.</summary>
    public string Fingerprint => FieldMasterKey.Fingerprint(_key.Value);

    /// <summary>The seal of a chain hash.</summary>
    public string Seal(string hash) => Convert.ToHexString(HMACSHA256.HashData(_key.Value, Encoding.ASCII.GetBytes(hash)));

    /// <summary>Whether <paramref name="mac"/> is this key's seal of <paramref name="hash"/>.</summary>
    public bool Sealed(string hash, string mac) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Seal(hash)), Encoding.ASCII.GetBytes(mac));

    /// <summary>The key as it is on disk, for a backup's escrow copy.</summary>
    internal byte[] Export() => (byte[])_key.Value.Clone();
}
