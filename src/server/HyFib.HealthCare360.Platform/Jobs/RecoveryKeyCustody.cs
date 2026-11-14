using System.Globalization;
using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// One proof that someone holds a recovery key (ASVS V11.1.1): they typed it in from their copy, either to put a new key
/// to use (<see cref="RecoveryKeyStore.Confirm"/>) or later, to show they still hold the current one
/// (<see cref="RecoveryKeyStore.Prove"/>). The key itself is never kept here, only its non-secret id. An append-only
/// register of who holds the key, so each line is in the sealed audit trail too.
/// </summary>
[Audited]
public sealed class RecoveryKeyProof
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The id of the key typed in (<see cref="BackupRecoveryKey.Id"/>).</summary>
    public string KeyId { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    /// <summary>The account's user name, as the audit log names it.</summary>
    public string? UserName { get; set; }

    public DateTimeOffset ProvenAt { get; set; }
}

internal sealed class RecoveryKeyProofConfiguration : IEntityTypeConfiguration<RecoveryKeyProof>
{
    public void Configure(EntityTypeBuilder<RecoveryKeyProof> b)
    {
        b.ToTable("backup_key_proofs", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.KeyId).HasMaxLength(BackupRecoveryKey.IdLength * 2);
        b.Property(x => x.UserName).HasMaxLength(128);
        b.HasIndex(x => new { x.KeyId, x.ProvenAt });
    }
}

/// <summary>Someone who has shown they hold the current recovery key, and when they last typed it in.</summary>
public sealed record RecoveryKeyCustodian(Guid UserId, string Name, DateTimeOffset LastProvenAt);

/// <summary>
/// The hospital's warnings about who holds the backups' recovery key, for System health (<see cref="RecoveryKeyCustody"/>):
/// about the hospital, not one server, since every server shares the key.
/// </summary>
public interface IRecoveryKeyCustodyWarnings
{
    Task<IReadOnlyList<HealthWarning>> WarningsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>The custody warnings of the key this server's backups use, from the proofs recorded in the database.</summary>
internal sealed class RecoveryKeyCustodyWarnings(HospitalDbContext db, RecoveryKeyStore store, IOptions<PlatformOptions> options) : IRecoveryKeyCustodyWarnings
{
    public Task<IReadOnlyList<HealthWarning>> WarningsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        RecoveryKeyCustody.WarningsAsync(db, store, now, PlatformServiceCollectionExtensions.ResolveTimeZone(options.Value.TimeZone), cancellationToken);
}

/// <summary>
/// Who holds the recovery key (DED-16). A key one person holds is lost with them: when they leave, fall ill or lose their
/// copy, no backup can be restored on new hardware. So System health warns until two different people have typed in the
/// current key (<see cref="CustodiansCode"/>), and again when nobody has for <see cref="CheckEvery"/>
/// (<see cref="UncheckedCode"/>), which is how the quarterly check of the copies is noticed when it is missed. Neither is
/// critical: nothing has failed, and the backups go on. Nothing is said while backups still use the original key, which
/// has warnings of its own.
/// </summary>
internal static class RecoveryKeyCustody
{
    public const string CustodiansCode = "backup.recovery_key_custodians";
    public const string UncheckedCode = "backup.recovery_key_unchecked";

    /// <summary>How many different people should hold the key.</summary>
    public const int Custodians = 2;

    /// <summary>How long the key may go without anyone typing it in: a quarter, and a few days' grace.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(90);

    private const string WhereToProve = "under Administration › Backups › Recovery key (I hold this key)";

    /// <summary>Each person who has proved <paramref name="keyId"/>, once, the one who did most recently first.</summary>
    public static IReadOnlyList<RecoveryKeyCustodian> Of(IEnumerable<RecoveryKeyProof> proofs, string keyId) =>
        proofs
            .Where(p => p.KeyId == keyId)
            .GroupBy(p => p.UserId)
            .Select(g =>
            {
                var last = g.MaxBy(p => p.ProvenAt)!;
                return new RecoveryKeyCustodian(g.Key, last.UserName ?? g.Key.ToString(), last.ProvenAt);
            })
            .OrderByDescending(c => c.LastProvenAt)
            .ToList();

    /// <summary>The custodians of <paramref name="keyId"/>, from the proofs recorded.</summary>
    public static async Task<IReadOnlyList<RecoveryKeyCustodian>> OfAsync(HospitalDbContext db, string keyId, CancellationToken ct) =>
        Of(await db.Set<RecoveryKeyProof>().AsNoTracking().Where(p => p.KeyId == keyId).ToListAsync(ct), keyId);

    /// <summary>
    /// The warnings about the current key, as System health gives them: none while backups use the original key (it has
    /// warnings of its own) or when the key cannot be read (the backups then fail, and say so).
    /// </summary>
    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(HospitalDbContext db, RecoveryKeyStore store, DateTimeOffset now, TimeZoneInfo zone, CancellationToken ct)
    {
        if (store.IsOriginal)
        {
            return [];
        }

        string keyId;
        try
        {
            keyId = store.CurrentKeyId();
        }
        catch (CryptographicException)
        {
            return [];
        }

        return Warnings(keyId, await OfAsync(db, keyId, ct), now, zone);
    }

    /// <summary>The warnings for <paramref name="custodians"/> of <paramref name="keyId"/> at <paramref name="now"/>, with dates in the hospital's <paramref name="zone"/>.</summary>
    public static IReadOnlyList<HealthWarning> Warnings(string keyId, IReadOnlyList<RecoveryKeyCustodian> custodians, DateTimeOffset now, TimeZoneInfo zone)
    {
        var warnings = new List<HealthWarning>();
        if (custodians.Count == 0)
        {
            warnings.Add(new(CustodiansCode, "warning",
                $"Nobody has shown they hold recovery key {keyId}, which opens the backups: if no copy of it can be found, they cannot be restored on new hardware. " +
                $"Two administrators who keep a copy each type it in {WhereToProve}, or make a new key there."));
            return warnings;
        }

        if (custodians.Count < Custodians)
        {
            warnings.Add(new(CustodiansCode, "warning",
                $"Only {custodians[0].Name} has shown they hold recovery key {keyId}: if they are away, leave or lose their copy, the backups cannot be restored on new hardware. " +
                $"A second administrator, with a copy of their own kept outside the building, types it in {WhereToProve}."));
        }

        var last = custodians.Max(c => c.LastProvenAt);
        if (now - last > CheckEvery)
        {
            warnings.Add(new(UncheckedCode, "warning",
                $"Nobody has typed in recovery key {keyId} since {TimeZoneInfo.ConvertTime(last, zone).ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}: " +
                $"check each copy can still be found and read, as the quarterly check asks, and type it in {WhereToProve}."));
        }

        return warnings;
    }
}
