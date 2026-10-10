using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Application-level encryption for sensitive identifiers (ABHA, Aadhaar last digits, policy numbers),
/// stored files and backups. AES-256-GCM with purpose-specific keys derived via HKDF from a master
/// key that is itself wrapped by ASP.NET Core Data Protection (machine DPAPI on Windows).
/// <para>
/// The master keys form a ring (SRS SEC-07): the original key, and one more for each rotation from System health. New
/// values are encrypted with the current key and name it; an earlier key stays in the ring, so anything encrypted
/// before stays readable, until <see cref="FieldKeyMover"/> has moved all of it to the current key. Then an
/// administrator may retire it (<see cref="IFieldKeyRing.Retire"/>). Blind indexes move with the key too, computed
/// again from the encrypted value they index. Fixed indexes and the keys from <see cref="DeriveKey"/> stay on the
/// original key, which is never retired: an Aadhaar index cannot be computed again, and older backups open with the
/// original recovery key, which comes from it (<see cref="Jobs.BackupRecoveryKey.Original"/>).
/// </para>
/// </summary>
public interface IFieldProtector
{
    /// <summary>
    /// Encrypts a short string: <c>v1.&lt;base64url(1|nonce|tag|ciphertext)&gt;</c> with the original key, or
    /// <c>v2.&lt;key id&gt;.&lt;base64url(2|key id|nonce|tag|ciphertext)&gt;</c> once the key has been rotated.
    /// </summary>
    string Protect(string plaintext);

    string? Unprotect(string? protectedValue);

    /// <summary>
    /// Deterministic keyed hash (HMAC-SHA256) for exact-match lookups without storing plaintext, with a key derived from
    /// the current key: 64 hex digits for the original key, <c>&lt;key id&gt;.&lt;base64url&gt;</c> (60 characters) for
    /// a later one, so an index names its key. Normalise the value (<see cref="Masking.Normalize"/>) before calling.
    /// Keep the value itself encrypted beside it and mark the column <c>IsBlindIndexOf</c> that one, so that after a
    /// rotation <see cref="FieldKeyMover"/> computes the index again with the new key. Look a value up with
    /// <see cref="BlindIndexes"/>.
    /// </summary>
    string BlindIndex(string normalizedValue);

    /// <summary>
    /// The value's <see cref="BlindIndex"/> under every key of the ring, to look it up: until the indexes have moved to
    /// the current key, a row may hold one made with an earlier key.
    /// </summary>
    IReadOnlyList<string> BlindIndexes(string normalizedValue);

    /// <summary>
    /// A blind index that stays on the original key (64 hex digits), for a value HC360 does not keep: an Aadhaar number,
    /// of which only the last four digits are kept, so its index cannot be computed again with a new key.
    /// </summary>
    string FixedBlindIndex(string normalizedValue);

    /// <summary>Encrypts bytes with the current key: <c>1|nonce|tag|ciphertext</c>, or <c>2|key id|nonce|tag|ciphertext</c> after a rotation.</summary>
    byte[] ProtectBytes(ReadOnlySpan<byte> plaintext, string purpose);

    /// <exception cref="CryptographicException">The data is corrupt, or was encrypted with a key this server does not have.</exception>
    byte[] UnprotectBytes(ReadOnlySpan<byte> protectedData, string purpose);

    /// <summary>
    /// A 256-bit key dedicated to <paramref name="purpose"/>, from the original master key, so it does not change when the
    /// key is rotated.
    /// </summary>
    byte[] DeriveKey(string purpose);
}

/// <summary>One master key of the ring.</summary>
public sealed record FieldKeyInfo(string Id, bool Original, bool Current, DateTimeOffset? CreatedAt);

/// <summary>The master key ring (SRS SEC-07): which key new values use, rotation, and moving values to the current key.</summary>
public interface IFieldKeyRing
{
    /// <summary>The key new values are encrypted with.</summary>
    string CurrentKeyId { get; }

    /// <summary>The key the ring started with: blind indexes, backups and every <c>v1</c> value use it.</summary>
    string OriginalKeyId { get; }

    /// <summary>
    /// When the current key became current, by a rotation or a restore; every earlier key was replaced then or before.
    /// Null while the original key is current.
    /// </summary>
    DateTimeOffset? CurrentSince { get; }

    IReadOnlyList<FieldKeyInfo> Keys { get; }

    /// <summary>
    /// The start every string encrypted with the current key has (<c>v1.</c> while it is the original key, otherwise
    /// <c>v2.&lt;key id&gt;.</c>), so values still to move can be found in the database.
    /// </summary>
    string CurrentPrefix { get; }

    /// <summary>The key these bytes (from <see cref="IFieldProtector.ProtectBytes"/>) were encrypted with.</summary>
    /// <exception cref="CryptographicException">They are not in a format the protector writes.</exception>
    string KeyIdOf(ReadOnlySpan<byte> protectedData);

    /// <summary>Makes a new master key the current one, and keeps every earlier key. Returns the new key's id.</summary>
    string Rotate();

    /// <summary>
    /// Takes a key a rotation added out of the ring, and deletes it from this machine. The caller checks first that
    /// nothing is encrypted with it any more: what is would no longer be readable here. Backups made before keep their
    /// copy of it.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is the original key, the current one, or not in the ring.</exception>
    void Retire(string id);
}

/// <summary>
/// The field master key itself, for the encrypted escrow copy every backup carries (DED-16): without it, encrypted
/// identifiers and documents could not be read on replacement hardware. Only the backup service uses it; never log it.
/// </summary>
internal interface IFieldKeyEscrow
{
    /// <summary>A copy of the original 32-byte master key.</summary>
    byte[] ExportMasterKey();

    /// <summary>The original key's non-secret fingerprint (<see cref="FieldMasterKey.Fingerprint"/>).</summary>
    string Fingerprint { get; }

    /// <summary>Copies of the keys added by rotations, and which key is current (null while it is the original).</summary>
    FieldKeyRingExport ExportRing();
}

/// <summary>The rotated keys of a ring, for a backup's escrow. <see cref="Current"/> is null while the original key is current.</summary>
public sealed record FieldKeyRingExport(IReadOnlyList<byte[]> Keys, string? Current);

internal sealed class FieldProtector : IFieldProtector, IFieldKeyEscrow, IFieldKeyRing
{
    private const byte OriginalKeyVersion = 1;
    private const byte KeyedVersion = 2;
    private const int KeyIdSize = 8;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly DataDirectories _directories;
    private readonly IDataProtector _wrapper;
    private readonly Lock _rotation = new();
    private readonly Lazy<byte[]> _masterKey;
    private readonly Lazy<FieldKeyRingState> _initialRing;
    private FieldKeyRingState? _ring;

    public FieldProtector(DataDirectories directories, IDataProtectionProvider dataProtection)
    {
        _directories = directories;
        _wrapper = FieldMasterKey.CreateProtector(dataProtection);
        var path = FieldMasterKey.PathIn(directories);
        _masterKey = new Lazy<byte[]>(() => FieldMasterKey.LoadOrCreate(path, _wrapper), LazyThreadSafetyMode.ExecutionAndPublication);
        _initialRing = new Lazy<FieldKeyRingState>(() => FieldKeyRingState.Load(directories, _wrapper, _masterKey.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private FieldKeyRingState Ring => Volatile.Read(ref _ring) ?? _initialRing.Value;

    public byte[] ExportMasterKey() => (byte[])_masterKey.Value.Clone();

    public string Fingerprint => FieldMasterKey.Fingerprint(_masterKey.Value);

    public FieldKeyRingExport ExportRing()
    {
        var ring = Ring;
        return new FieldKeyRingExport(
            ring.Keys.Where(k => k.Key != ring.OriginalId).Select(k => (byte[])k.Value.Clone()).ToList(),
            ring.IsOriginalCurrent ? null : ring.CurrentId);
    }

    public string CurrentKeyId => Ring.CurrentId;

    public string OriginalKeyId => Ring.OriginalId;

    public DateTimeOffset? CurrentSince => FieldKeyRing.CurrentSince(_directories);

    public IReadOnlyList<FieldKeyInfo> Keys
    {
        get
        {
            var ring = Ring;
            return ring.Keys.Keys
                .Select(id => new FieldKeyInfo(id, id == ring.OriginalId, id == ring.CurrentId, FieldKeyRing.CreatedAt(_directories, id, id == ring.OriginalId)))
                .OrderBy(k => k.CreatedAt)
                .ToList();
        }
    }

    public string CurrentPrefix => Ring.IsOriginalCurrent ? "v1." : $"v2.{Ring.CurrentId}.";

    public string KeyIdOf(ReadOnlySpan<byte> protectedData) =>
        protectedData.Length >= 1 + NonceSize + TagSize && protectedData[0] == OriginalKeyVersion ? Ring.OriginalId
        : protectedData.Length >= 1 + KeyIdSize + NonceSize + TagSize && protectedData[0] == KeyedVersion ? Convert.ToHexString(protectedData.Slice(1, KeyIdSize))
        : throw new CryptographicException("Unsupported or corrupt protected payload.");

    public string Rotate()
    {
        lock (_rotation)
        {
            var ring = Ring;
            byte[] key;
            string id;
            do
            {
                key = RandomNumberGenerator.GetBytes(FieldMasterKey.Length);
                id = FieldMasterKey.Fingerprint(key);
            }
            while (ring.Keys.ContainsKey(id));

            // The key is on disk before anything is encrypted with it, and it is current only once it is there.
            FieldKeyRing.Add(_directories, _wrapper, key);
            FieldKeyRing.SetCurrent(_directories, id);
            Volatile.Write(ref _ring, ring.With(id, key));
            return id;
        }
    }

    public void Retire(string id)
    {
        lock (_rotation)
        {
            var ring = Ring;
            if (id == ring.OriginalId || id == ring.CurrentId || !ring.Keys.ContainsKey(id))
            {
                throw new InvalidOperationException(id == ring.OriginalId ? $"Key {id} is the original key, which is never retired."
                    : id == ring.CurrentId ? $"Key {id} is the current key."
                    : $"Key {id} is not in the ring.");
            }

            // Off the disk first, so a restart never brings it back. The bytes are left for the collector, not wiped: a
            // backup may be copying them from the ring it read a moment ago.
            FieldKeyRing.Remove(_directories, id);
            Volatile.Write(ref _ring, ring.Without(id));
        }
    }

    public string Protect(string plaintext)
    {
        var data = ProtectBytes(Encoding.UTF8.GetBytes(plaintext), "field");
        return data[0] == OriginalKeyVersion
            ? "v1." + Base64UrlEncoder.Encode(data)
            : $"v2.{Convert.ToHexString(data, 1, KeyIdSize)}.{Base64UrlEncoder.Encode(data)}";
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return protectedValue;
        }

        // The key id in a v2 prefix only lets the database find values to move; the payload names its own key.
        var payload = protectedValue.StartsWith("v1.", StringComparison.Ordinal) ? protectedValue[3..]
            : protectedValue.StartsWith("v2.", StringComparison.Ordinal) && protectedValue.IndexOf('.', 3) is var dot and > 3 ? protectedValue[(dot + 1)..]
            : throw new CryptographicException("Unsupported protected value format.");
        return Encoding.UTF8.GetString(UnprotectBytes(Base64UrlEncoder.DecodeBytes(payload), "field"));
    }

    public string BlindIndex(string normalizedValue)
    {
        var ring = Ring;
        return BlindIndexWith(ring, ring.CurrentId, normalizedValue);
    }

    public IReadOnlyList<string> BlindIndexes(string normalizedValue)
    {
        var ring = Ring;
        return ring.Keys.Keys.Select(id => BlindIndexWith(ring, id, normalizedValue)).ToList();
    }

    public string FixedBlindIndex(string normalizedValue)
    {
        var ring = Ring;
        return BlindIndexWith(ring, ring.OriginalId, normalizedValue);
    }

    // The original key's index is 64 hex digits, as before keys rotated; a later key's names it, and fits the same column.
    private static string BlindIndexWith(FieldKeyRingState ring, string keyId, string normalizedValue)
    {
        var hash = HMACSHA256.HashData(FieldMasterKey.DeriveKey(ring.Keys[keyId], "blind-index"), Encoding.UTF8.GetBytes(normalizedValue));
        return keyId == ring.OriginalId ? Convert.ToHexString(hash) : $"{keyId}.{Base64UrlEncoder.Encode(hash)}";
    }

    public byte[] ProtectBytes(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var ring = Ring;
        var header = ring.IsOriginalCurrent ? 1 : 1 + KeyIdSize;
        var output = new byte[header + NonceSize + TagSize + plaintext.Length];
        if (ring.IsOriginalCurrent)
        {
            output[0] = OriginalKeyVersion;
        }
        else
        {
            output[0] = KeyedVersion;
            Convert.FromHexString(ring.CurrentId).CopyTo(output, 1);
        }

        var nonce = output.AsSpan(header, NonceSize);
        var tag = output.AsSpan(header + NonceSize, TagSize);
        var cipher = output.AsSpan(header + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(FieldMasterKey.DeriveKey(ring.Keys[ring.CurrentId], purpose), TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return output;
    }

    public byte[] UnprotectBytes(ReadOnlySpan<byte> protectedData, string purpose)
    {
        var ring = Ring;
        byte[] masterKey;
        int header;
        if (protectedData.Length >= 1 + NonceSize + TagSize && protectedData[0] == OriginalKeyVersion)
        {
            masterKey = ring.Keys[ring.OriginalId];
            header = 1;
        }
        else if (protectedData.Length >= 1 + KeyIdSize + NonceSize + TagSize && protectedData[0] == KeyedVersion)
        {
            var id = Convert.ToHexString(protectedData.Slice(1, KeyIdSize));
            masterKey = ring.Keys.TryGetValue(id, out var key) ? key
                : throw new CryptographicException($"The data was encrypted with key {id}, which this server does not have.");
            header = 1 + KeyIdSize;
        }
        else
        {
            throw new CryptographicException("Unsupported or corrupt protected payload.");
        }

        var nonce = protectedData.Slice(header, NonceSize);
        var tag = protectedData.Slice(header + NonceSize, TagSize);
        var cipher = protectedData[(header + NonceSize + TagSize)..];
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(FieldMasterKey.DeriveKey(masterKey, purpose), TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    public byte[] DeriveKey(string purpose) => FieldMasterKey.DeriveKey(_masterKey.Value, purpose);

    /// <summary>The keys by id, the original key's id, and the current key's id. Replaced whole on a rotation.</summary>
    private sealed record FieldKeyRingState(IReadOnlyDictionary<string, byte[]> Keys, string OriginalId, string CurrentId)
    {
        public bool IsOriginalCurrent => CurrentId == OriginalId;

        public static FieldKeyRingState Load(DataDirectories directories, IDataProtector wrapper, byte[] original)
        {
            var originalId = FieldMasterKey.Fingerprint(original);
            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [originalId] = original };
            foreach (var key in FieldKeyRing.Load(directories, wrapper))
            {
                keys.TryAdd(FieldMasterKey.Fingerprint(key), key);
            }

            var current = FieldKeyRing.Current(directories) ?? originalId;
            return keys.ContainsKey(current)
                ? new FieldKeyRingState(keys, originalId, current)
                : throw new CryptographicException($"The current field key {current} is missing from {FieldKeyRing.DirectoryIn(directories)}.");
        }

        public FieldKeyRingState With(string id, byte[] key) =>
            new(new Dictionary<string, byte[]>(Keys, StringComparer.Ordinal) { [id] = key }, OriginalId, id);

        public FieldKeyRingState Without(string id)
        {
            var keys = new Dictionary<string, byte[]>(Keys, StringComparer.Ordinal);
            keys.Remove(id);
            return new FieldKeyRingState(keys, OriginalId, CurrentId);
        }
    }
}

/// <summary>
/// The field master key on disk (<c>keys/field-master.key</c>): 32 random bytes wrapped by Data Protection, which uses
/// machine DPAPI on Windows, so the file cannot be read on another machine. Backups carry an escrow copy so the key can
/// be installed again on replacement hardware (<see cref="Jobs.BackupRestore"/>).
/// </summary>
public static class FieldMasterKey
{
    public const string FileName = "field-master.key";
    public const int Length = 32;
    private const string ProtectorPurpose = "HC360.FieldProtection.MasterKey.v1";

    public static string PathIn(DataDirectories directories) => Path.Combine(directories.Keys, FileName);

    public static IDataProtector CreateProtector(IDataProtectionProvider dataProtection) => dataProtection.CreateProtector(ProtectorPurpose);

    /// <summary>The key for one purpose (HKDF-SHA256), e.g. "field", "files", "backup".</summary>
    public static byte[] DeriveKey(byte[] masterKey, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.UTF8.GetBytes("hc360:" + purpose));

    /// <summary>
    /// A short, non-secret identifier of a master key (16 hex characters). Backups record it, and a restore checks that
    /// the key it installs is the one the backup's data was encrypted with.
    /// </summary>
    public static string Fingerprint(byte[] masterKey) => Convert.ToHexString(DeriveKey(masterKey, "key-fingerprint"))[..16];

    /// <summary>The key on this machine, or null when none has been created yet.</summary>
    public static byte[]? Load(string path, IDataProtector protector) =>
        File.Exists(path) ? protector.Unprotect(File.ReadAllBytes(path)) : null;

    internal static byte[] LoadOrCreate(string path, IDataProtector protector)
    {
        if (Load(path, protector) is { } existing)
        {
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(Length);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(key));
        File.Move(temp, path, overwrite: false);
        return key;
    }

    /// <summary>
    /// Installs a recovered master key, wrapped for this machine. A different key already here is kept under a new name
    /// (<c>field-master.key.replaced-&lt;time&gt;</c>), never deleted, because data encrypted with it may still be needed.
    /// The new key is written before the current one is moved aside, and the current one is put back if the new one cannot
    /// take its place, so the machine is never left without a key (the service would otherwise create a new, empty one).
    /// </summary>
    /// <returns>The name the previous key was kept under, or null when there was none (or it was the same key).</returns>
    public static string? Install(string path, IDataProtector protector, byte[] masterKey, DateTimeOffset now)
    {
        if (masterKey.Length != Length)
        {
            throw new CryptographicException("A field master key must be 32 bytes.");
        }

        var existing = Load(path, protector);
        if (existing is not null && CryptographicOperations.FixedTimeEquals(existing, masterKey))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(masterKey));

        string? keptAs = null;
        try
        {
            if (existing is not null)
            {
                keptAs = $"{path}.replaced-{now.UtcDateTime:yyyyMMdd-HHmmss}";
                File.Move(path, keptAs, overwrite: false);
            }

            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            if (keptAs is not null && File.Exists(keptAs) && !File.Exists(path))
            {
                File.Move(keptAs, path);
            }

            File.Delete(temp);
            throw;
        }

        return keptAs is null ? null : Path.GetFileName(keptAs);
    }
}

/// <summary>
/// The keys rotations added to the field master key ring (SRS SEC-07), beside the original key: each in
/// <c>keys/field-keys/&lt;key id&gt;.key</c>, wrapped by Data Protection like the original, and the current key's id in
/// <c>keys/field-keys/current</c> (none while the original is current). An earlier key stays until nothing is encrypted
/// with it any more and an administrator retires it (<see cref="Remove"/>). Every backup carries a copy of each key the
/// ring held when it was made, so a backup restores without the machine still having its keys.
/// </summary>
public static class FieldKeyRing
{
    public const string FolderName = "field-keys";
    public const string CurrentFileName = "current";
    private const string KeyExtension = ".key";

    public static string DirectoryIn(DataDirectories directories) => Path.Combine(directories.Keys, FolderName);

    /// <summary>The keys added by rotations, unwrapped. A file whose key does not match its name is refused.</summary>
    public static IReadOnlyList<byte[]> Load(DataDirectories directories, IDataProtector wrapper)
    {
        var folder = DirectoryIn(directories);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var keys = new List<byte[]>();
        foreach (var path in Directory.EnumerateFiles(folder, "*" + KeyExtension).Order(StringComparer.Ordinal))
        {
            var key = wrapper.Unprotect(File.ReadAllBytes(path));
            if (key.Length != FieldMasterKey.Length || FieldMasterKey.Fingerprint(key) != Path.GetFileNameWithoutExtension(path))
            {
                throw new CryptographicException($"The field key {Path.GetFileName(path)} does not match its name.");
            }

            keys.Add(key);
        }

        return keys;
    }

    /// <summary>The current key's id, or null while the original key is current.</summary>
    public static string? Current(DataDirectories directories)
    {
        var path = Path.Combine(DirectoryIn(directories), CurrentFileName);
        return File.Exists(path) ? File.ReadAllText(path).Trim().ToUpperInvariant() : null;
    }

    /// <summary>When the current key was made current (its id written down), or null while the original key is current.</summary>
    public static DateTimeOffset? CurrentSince(DataDirectories directories)
    {
        var path = Path.Combine(DirectoryIn(directories), CurrentFileName);
        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }

    /// <summary>Adds a key, wrapped for this machine, unless it is there already. Returns its id.</summary>
    public static string Add(DataDirectories directories, IDataProtector wrapper, byte[] key)
    {
        if (key.Length != FieldMasterKey.Length)
        {
            throw new CryptographicException("A field master key must be 32 bytes.");
        }

        var id = FieldMasterKey.Fingerprint(key);
        var folder = DirectoryIn(directories);
        var path = Path.Combine(folder, id + KeyExtension);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, wrapper.Protect(key));
            File.Move(temp, path, overwrite: false);
        }

        return id;
    }

    /// <summary>
    /// Deletes a key a rotation added, which must not be the current one. Data still encrypted with it is no longer
    /// readable here, so the caller checks first that there is none.
    /// </summary>
    public static void Remove(DataDirectories directories, string id)
    {
        if (id.Length != 16 || !id.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A field key id is 16 hexadecimal digits.", nameof(id));
        }

        if (string.Equals(Current(directories), id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Key {id} is the current key.");
        }

        File.Delete(Path.Combine(DirectoryIn(directories), id.ToUpperInvariant() + KeyExtension));
    }

    /// <summary>Makes the key with this id current (it must have been added), or the original key when null.</summary>
    public static void SetCurrent(DataDirectories directories, string? id)
    {
        var path = Path.Combine(DirectoryIn(directories), CurrentFileName);
        if (id is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        Directory.CreateDirectory(DirectoryIn(directories));
        var temp = path + ".tmp";
        File.WriteAllText(temp, id);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>When a key was made: when its file was written.</summary>
    public static DateTimeOffset? CreatedAt(DataDirectories directories, string id, bool original)
    {
        var path = original ? FieldMasterKey.PathIn(directories) : Path.Combine(DirectoryIn(directories), id + KeyExtension);
        return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero) : null;
    }
}

/// <summary>
/// The Data Protection key ring of the Server Node (<c>keys/dataprotection</c>, machine DPAPI on Windows). The web host
/// registers it through DI; code that runs outside the host (the TLS bootstrap and the Server CLI) uses
/// <see cref="CreateProvider"/> so it wraps and unwraps keys exactly as the service does.
/// </summary>
public static class HospitalDataProtection
{
    public const string ApplicationName = "HyFib.HealthCare360";

    public static string KeyRingDirectory(DataDirectories directories) => Path.Combine(directories.Keys, "dataprotection");

    public static IDataProtectionProvider CreateProvider(DataDirectories directories) =>
        DataProtectionProvider.Create(new DirectoryInfo(KeyRingDirectory(directories)), protection =>
        {
            protection.SetApplicationName(ApplicationName);
            if (OperatingSystem.IsWindows())
            {
                protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
            }
        });
}

/// <summary>Helpers to keep masked display values consistent across modules.</summary>
public static class Masking
{
    /// <summary>Shows only the last <paramref name="visible"/> characters: "XXXX-XXXX-1234".</summary>
    public static string MaskIdentifier(string? value, int visible = 4)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length <= visible ? compact : new string('X', compact.Length - visible) + compact[^visible..];
    }

    /// <summary>Upper-case alphanumerics only — the canonical form for blind-index lookups.</summary>
    public static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
