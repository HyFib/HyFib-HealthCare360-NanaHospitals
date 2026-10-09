using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// The key that opens backups (DED-16; ASVS V11.1.1, V13.3.2): 32 random bytes of its own, not derived from the data
/// encryption keys, so it can be replaced, and shown once only, to the administrator who makes it. Until the first one is
/// made, backups use the original recovery key, which is derived from the original data key (<see cref="Original"/>).
/// <para>
/// It is written down as 14 groups of 4 letters and digits (Crockford base32, which has no I, L, O or U). The last group
/// checks the others, so a mistake in a copy is found when the key is typed back, not on the day of a restore.
/// </para>
/// </summary>
public static class BackupRecoveryKey
{
    public const int Length = 32;

    /// <summary>The length of a key's id (<see cref="Id"/>), in bytes; backup files carry it in their header.</summary>
    public const int IdLength = 8;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    // 256 bits at 5 bits a character; the last character's 4 spare bits are zero.
    private const int KeyCharacters = 52;

    // 20 bits of the key's SHA-256.
    private const int CheckCharacters = 4;
    private const int GroupLength = 4;

    /// <summary>
    /// The key's non-secret id, 16 hexadecimal characters: backup files, the backup list and the restore tool name the key
    /// that opens a backup by it.
    /// </summary>
    public static string Id(byte[] key) => Convert.ToHexString(FieldMasterKey.DeriveKey(key, "recovery-key-id"), 0, IdLength);

    /// <summary>The original recovery key: the backup key derived from the original data key, as every backup used before.</summary>
    public static byte[] Original(byte[] masterKey) => FieldMasterKey.DeriveKey(masterKey, "backup");

    public static byte[] Create() => RandomNumberGenerator.GetBytes(Length);

    /// <summary>The key as it is shown and written down: <c>7KQ2-M9XA-…</c>, 14 groups of 4.</summary>
    public static string Format(byte[] key)
    {
        if (key.Length != Length)
        {
            throw new ArgumentException("A recovery key is 32 bytes.", nameof(key));
        }

        var characters = new char[KeyCharacters + CheckCharacters];
        Encode(key, characters.AsSpan(0, KeyCharacters));
        Encode(Check(key), characters.AsSpan(KeyCharacters));
        return string.Join('-', characters.Chunk(GroupLength).Select(group => new string(group)));
    }

    /// <summary>
    /// Reads a key as someone types it: the grouped form, where case, spaces and dashes do not matter and O reads as 0 and I
    /// or L as 1, or the 44-character form the original recovery key was shown in.
    /// </summary>
    /// <param name="problem">What is wrong with the text, in words for the person who typed it.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out byte[]? key, [NotNullWhen(false)] out string? problem)
    {
        key = null;
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            problem = "Enter the recovery key.";
            return false;
        }

        var compact = Compact(trimmed);
        if (compact.Length == KeyCharacters + CheckCharacters)
        {
            var bytes = new byte[Length];
            var check = new char[CheckCharacters];
            if (compact.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) && Decode(compact.AsSpan(0, KeyCharacters), bytes))
            {
                Encode(Check(bytes), check);
                if (compact.AsSpan(KeyCharacters).SequenceEqual(check))
                {
                    key = bytes;
                    problem = null;
                    return true;
                }
            }

            CryptographicOperations.ZeroMemory(bytes);
            problem = "The recovery key has a mistake in it. Check it against your copy.";
            return false;
        }

        // The original recovery key was shown as base64.
        var original = new byte[Length];
        if (trimmed.Length == 44 && Convert.TryFromBase64String(trimmed, original, out var written) && written == Length)
        {
            key = original;
            problem = null;
            return true;
        }

        problem = "That is not an HC360 recovery key. A recovery key is 14 groups of 4 letters and digits; the original key, " +
            "shown before HC360 made separate recovery keys, is 44 characters long.";
        return false;
    }

    private static string Compact(string text)
    {
        var compact = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c == '-')
            {
                continue;
            }

            compact.Append(char.ToUpperInvariant(c) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var upper => upper,
            });
        }

        return compact.ToString();
    }

    private static byte[] Check(byte[] key) => SHA256.HashData(key);

    /// <summary>Writes the leading bits of <paramref name="data"/>, 5 to a character, padding with zero bits.</summary>
    private static void Encode(ReadOnlySpan<byte> data, Span<char> output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            var value = 0;
            for (var bit = i * 5; bit < (i * 5) + 5; bit++)
            {
                var set = bit / 8 < data.Length && (data[bit / 8] & (0x80 >> (bit % 8))) != 0;
                value = (value << 1) | (set ? 1 : 0);
            }

            output[i] = Alphabet[value];
        }
    }

    /// <summary>Reads the bits back into <paramref name="output"/>; false when a padding bit is set.</summary>
    private static bool Decode(ReadOnlySpan<char> characters, Span<byte> output)
    {
        output.Clear();
        for (var i = 0; i < characters.Length; i++)
        {
            var value = Alphabet.IndexOf(characters[i], StringComparison.Ordinal);
            for (var b = 0; b < 5; b++)
            {
                var bit = (i * 5) + b;
                var set = (value & (0x10 >> b)) != 0;
                if (bit / 8 < output.Length)
                {
                    if (set)
                    {
                        output[bit / 8] |= (byte)(0x80 >> (bit % 8));
                    }
                }
                else if (set)
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>A backup opens with a different recovery key from the one given; both are named by their ids.</summary>
public sealed class BackupKeyMismatchException(string backupKeyId, string givenKeyId)
    : CryptographicException($"This backup opens with recovery key {backupKeyId}, and the key given is {givenKeyId}.")
{
    public string BackupKeyId { get; } = backupKeyId;

    public string GivenKeyId { get; } = givenKeyId;
}

/// <summary>The key new backups are encrypted with. Whoever reads <see cref="Key"/> zeroes it when done.</summary>
/// <param name="Original">True while backups still use the original recovery key, derived from the original data key.</param>
/// <param name="Since">When it was made; null for the original key.</param>
internal sealed record CurrentRecoveryKey(string KeyId, byte[] Key, bool Original, DateTimeOffset? Since);

/// <summary>The recovery key new backups use, for the Backups page.</summary>
/// <param name="Original">Backups still use the original recovery key, derived from the original data key.</param>
/// <param name="Since">Since when this server has used the key; null for the original key.</param>
/// <param name="By">Who made it, from the audit log.</param>
/// <param name="OriginalShown">
/// For the original key: whether it was ever shown, when administrators could still display it. When it never was,
/// nobody holds a key that opens the backups.
/// </param>
/// <param name="OriginalKeyId">The original key's id, which the backups made with it name.</param>
public sealed record RecoveryKeyStatus(string KeyId, bool Original, DateTimeOffset? Since, string? By, bool OriginalShown, string OriginalKeyId);

/// <summary>A new recovery key, shown this once. It replaces the current key only when it is typed back by <see cref="ExpiresAt"/>.</summary>
public sealed record NewRecoveryKey(string KeyId, string Key, DateTimeOffset ExpiresAt);

/// <summary>The new recovery key, typed back from the copy the administrator made.</summary>
public sealed record ConfirmRecoveryKeyRequest(string Key);

/// <summary>
/// The recovery key on the Server Node: <c>keys/backup-recovery.key</c>, wrapped by Data Protection like the other keys;
/// without that file, backups use the original key. A new key waits in memory only, for the administrator it was shown to,
/// until they type it back from their copy; then it replaces the file. The key it replaces is not kept: this server never
/// opens an older backup, and the hospital keeps every key it was shown for as long as backups made with it are kept.
/// </summary>
internal sealed class RecoveryKeyStore(DataDirectories directories, IDataProtectionProvider dataProtection, IFieldKeyEscrow escrow, TimeProvider time)
{
    public const string FileName = "backup-recovery.key";
    private const string ProtectorPurpose = "HC360.Backup.RecoveryKey.v1";

    /// <summary>How long a new key waits to be typed back before it is dropped.</summary>
    public static readonly TimeSpan ConfirmWithin = TimeSpan.FromMinutes(30);

    private readonly IDataProtector _wrapper = dataProtection.CreateProtector(ProtectorPurpose);
    private readonly Lock _gate = new();
    private PendingKey? _pending;

    private string FilePath => PathIn(directories);

    /// <summary>True while backups use the original key, derived from the original data key.</summary>
    public bool IsOriginal => !File.Exists(FilePath);

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Keys, FileName);

    /// <summary>
    /// After a restore (<see cref="BackupRestore"/>): backups go on using the key the backup was opened with, when this
    /// machine has no recovery key of its own, as on new hardware. A key already here is kept, because it may have replaced
    /// the one an older backup was made with. The original key needs no file.
    /// </summary>
    /// <param name="masterKey">The original data key the restore installed, which the original recovery key comes from.</param>
    /// <returns>True when the key was installed.</returns>
    public static bool KeepAfterRestore(DataDirectories directories, IDataProtectionProvider dataProtection, byte[] key, byte[] masterKey)
    {
        var path = PathIn(directories);
        var original = BackupRecoveryKey.Original(masterKey);
        var isOriginal = CryptographicOperations.FixedTimeEquals(key, original);
        CryptographicOperations.ZeroMemory(original);
        if (File.Exists(path) || isOriginal)
        {
            return false;
        }

        Write(path, dataProtection.CreateProtector(ProtectorPurpose), key);
        return true;
    }

    /// <summary>The key new backups use: the last one confirmed, or else the original key.</summary>
    public CurrentRecoveryKey Current()
    {
        if (File.Exists(FilePath))
        {
            var key = _wrapper.Unprotect(File.ReadAllBytes(FilePath));
            if (key.Length != BackupRecoveryKey.Length)
            {
                throw new CryptographicException($"The backup recovery key in {FilePath} is damaged.");
            }

            return new CurrentRecoveryKey(BackupRecoveryKey.Id(key), key, Original: false, new DateTimeOffset(File.GetLastWriteTimeUtc(FilePath), TimeSpan.Zero));
        }

        var original = Original();
        return new CurrentRecoveryKey(BackupRecoveryKey.Id(original), original, Original: true, Since: null);
    }

    /// <summary>The original key's id, which the backups made with it name.</summary>
    public string OriginalKeyId()
    {
        var original = Original();
        try
        {
            return BackupRecoveryKey.Id(original);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
        }
    }

    /// <summary>
    /// Makes a new key, to be shown to <paramref name="userId"/> and typed back by them within <see cref="ConfirmWithin"/>.
    /// A key made before and not typed back is dropped.
    /// </summary>
    public NewRecoveryKey Create(Guid userId)
    {
        var key = BackupRecoveryKey.Create();
        var pending = new PendingKey(BackupRecoveryKey.Id(key), key, userId, time.GetUtcNow() + ConfirmWithin);
        lock (_gate)
        {
            Drop();
            _pending = pending;
        }

        return new NewRecoveryKey(pending.KeyId, BackupRecoveryKey.Format(key), pending.ExpiresAt);
    }

    /// <summary>
    /// Makes the new key current once the administrator it was shown to types it back. Returns the id of the key it
    /// replaces, which still opens the backups made before.
    /// </summary>
    public Result<string> Confirm(string keyId, string? typed, Guid userId)
    {
        lock (_gate)
        {
            if (_pending is not { } pending || pending.KeyId != keyId || pending.UserId != userId || time.GetUtcNow() >= pending.ExpiresAt)
            {
                return Error.Rule("backup.recovery_key_not_waiting",
                    "This recovery key is no longer waiting to be confirmed, so nothing changed. Make a new one.");
            }

            if (!BackupRecoveryKey.TryParse(typed, out var key, out var problem))
            {
                return Error.Field("key", problem);
            }

            var matches = CryptographicOperations.FixedTimeEquals(key, pending.Key);
            CryptographicOperations.ZeroMemory(key);
            if (!matches)
            {
                return Error.Field("key", "This is not the new key. Go back, check your copy against it, and type it again.");
            }

            var previous = Current();
            CryptographicOperations.ZeroMemory(previous.Key);
            Write(FilePath, _wrapper, pending.Key);
            Drop();
            return previous.KeyId;
        }
    }

    private byte[] Original()
    {
        var masterKey = escrow.ExportMasterKey();
        try
        {
            return BackupRecoveryKey.Original(masterKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    private static void Write(string path, IDataProtector wrapper, byte[] key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, wrapper.Protect(key));
        File.Move(temp, path, overwrite: true);
    }

    private void Drop()
    {
        if (_pending is { } pending)
        {
            CryptographicOperations.ZeroMemory(pending.Key);
            _pending = null;
        }
    }

    private sealed record PendingKey(string KeyId, byte[] Key, Guid UserId, DateTimeOffset ExpiresAt);
}

/// <summary>
/// Making a new recovery key from the Backups page (ASVS V11.1.1, V13.3.2), by a holder of <c>admin.system.manage</c> who
/// gives their password again. The key is shown once, to them alone, and becomes current only once they type it back from
/// their copy. Each step is audited.
/// </summary>
internal sealed class RecoveryKeyService(RecoveryKeyStore store, AuthService auth, ICurrentUser user, IAuditLogger audit, HospitalDbContext db)
{
    public const string Shown = "backup.recovery_key_shown";
    public const string Replaced = "backup.recovery_key_replaced";

    /// <summary>Recorded each time the original key was displayed, when any holder of the backups permission could.</summary>
    public const string OriginalDisplayed = "backup.recovery_key_exported";

    /// <summary>
    /// Whether the original key was ever displayed. When it never was, nobody holds a key that opens the backups made with
    /// it. (Security events only, which the audit log's index on category keeps to a few.)
    /// </summary>
    public static Task<bool> OriginalWasDisplayedAsync(HospitalDbContext db, CancellationToken ct) =>
        db.Set<AuditEvent>().AsNoTracking().AnyAsync(e => e.Category == AuditCategories.Security && e.Action == OriginalDisplayed, ct);

    public async Task<RecoveryKeyStatus> StatusAsync(CancellationToken ct)
    {
        var current = store.Current();
        CryptographicOperations.ZeroMemory(current.Key);
        if (current.Original)
        {
            var shown = await OriginalWasDisplayedAsync(db, ct);
            return new RecoveryKeyStatus(current.KeyId, Original: true, Since: null, By: null, shown, current.KeyId);
        }

        var by = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Security && e.Action == Replaced)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.UserName)
            .FirstOrDefaultAsync(ct);
        return new RecoveryKeyStatus(current.KeyId, Original: false, current.Since, by, OriginalShown: false, store.OriginalKeyId());
    }

    public async Task<Result<NewRecoveryKey>> CreateAsync(ReauthenticateRequest request, CancellationToken ct)
    {
        var confirmed = await auth.ConfirmPasswordAsync(request, ct);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        var created = store.Create(user.UserId!.Value);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, Shown,
            $"A new backup recovery key, {created.KeyId}, was shown; backups use it once it is typed back")
        {
            Severity = AuditSeverity.Critical,
        }, CancellationToken.None);
        return created;
    }

    public async Task<Result<RecoveryKeyStatus>> ConfirmAsync(string keyId, ConfirmRecoveryKeyRequest request, CancellationToken ct)
    {
        var replaced = store.Confirm(keyId, request.Key, user.UserId!.Value);
        if (replaced.IsFailure)
        {
            return replaced.Error!;
        }

        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, Replaced,
            $"Backups now use recovery key {keyId} instead of {replaced.Value}, which still opens the backups made before")
        {
            Severity = AuditSeverity.Critical,
        }, CancellationToken.None);
        return await StatusAsync(ct);
    }
}
