using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>
    /// Reads the ring again now when another node sharing the key folder changed it (SCAL-01); nothing on a single Server
    /// Node. A node of several also looks by itself every <see cref="FieldProtector.SharedCheckInterval"/>.
    /// </summary>
    void Refresh();
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

/// <remarks>
/// With several nodes the ring is in the folder they share (SCAL-01), and a rotation or a retirement on one node reaches
/// the others: each reads the ring again when it has changed, looking at most every <see cref="SharedCheckInterval"/>, and
/// at once (at most once a second) when a value names a key it does not have. Until then a node encrypts with the key that
/// was current, which every node can read: an earlier key is retired only a day after the rotation.
/// </remarks>
internal sealed class FieldProtector : IFieldProtector, IFieldKeyEscrow, IFieldKeyRing
{
    /// <summary>How often a node of several looks whether another one changed the ring.</summary>
    public static readonly TimeSpan SharedCheckInterval = TimeSpan.FromSeconds(10);

    private const byte OriginalKeyVersion = 1;
    private const byte KeyedVersion = 2;
    private const int KeyIdSize = 8;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly TimeSpan UnknownKeyCheckInterval = TimeSpan.FromSeconds(1);
    private readonly DataDirectories _directories;
    private readonly IDataProtector _wrapper;
    private readonly bool _shared;
    private readonly Lock _rotation = new();
    private readonly Lazy<byte[]> _masterKey;
    private readonly Lazy<FieldKeyRingState> _initialRing;
    private FieldKeyRingState? _ring;
    private long _checkedAt;
    private long _unknownCheckedAt;

    public FieldProtector(DataDirectories directories, IDataProtectionProvider dataProtection)
    {
        _directories = directories;
        _wrapper = FieldMasterKey.CreateProtector(dataProtection);
        _shared = directories.IsShared;
        var path = FieldMasterKey.PathIn(directories);
        _masterKey = new Lazy<byte[]>(() => FieldMasterKey.LoadOrCreate(path, _wrapper), LazyThreadSafetyMode.ExecutionAndPublication);
        _initialRing = new Lazy<FieldKeyRingState>(() => FieldKeyRingState.Load(directories, _wrapper, _masterKey.Value), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private FieldKeyRingState Ring
    {
        get
        {
            var ring = Volatile.Read(ref _ring) ?? _initialRing.Value;
            return _shared && Due(ref _checkedAt, SharedCheckInterval) ? ReloadIfChanged(ring) : ring;
        }
    }

    public void Refresh()
    {
        if (_shared)
        {
            ReloadIfChanged(Volatile.Read(ref _ring) ?? _initialRing.Value);
        }
    }

    /// <summary>
    /// The ring as it is now in the shared folder, when another node changed it since it was read. A folder that cannot be
    /// read at the moment, or a change half written, leaves the ring as it was until the next look.
    /// </summary>
    private FieldKeyRingState ReloadIfChanged(FieldKeyRingState ring)
    {
        lock (_rotation)
        {
            var current = Volatile.Read(ref _ring) ?? ring;
            try
            {
                if (FieldKeyRing.Stamp(_directories) == current.Stamp)
                {
                    return current;
                }

                var reloaded = FieldKeyRingState.Load(_directories, _wrapper, _masterKey.Value);
                Volatile.Write(ref _ring, reloaded);
                return reloaded;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                return current;
            }
        }
    }

    private static bool Due(ref long checkedAt, TimeSpan interval)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref checkedAt);
        return now - last >= (long)interval.TotalMilliseconds && Interlocked.CompareExchange(ref checkedAt, now, last) == last;
    }

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
        Refresh();
        lock (_rotation)
        {
            var ring = Volatile.Read(ref _ring) ?? _initialRing.Value;
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
            Volatile.Write(ref _ring, ring.With(id, key, FieldKeyRing.Stamp(_directories)));
            return id;
        }
    }

    public void Retire(string id)
    {
        Refresh();
        lock (_rotation)
        {
            var ring = Volatile.Read(ref _ring) ?? _initialRing.Value;
            if (id == ring.OriginalId || id == ring.CurrentId || !ring.Keys.ContainsKey(id))
            {
                throw new InvalidOperationException(id == ring.OriginalId ? $"Key {id} is the original key, which is never retired."
                    : id == ring.CurrentId ? $"Key {id} is the current key."
                    : $"Key {id} is not in the ring.");
            }

            // Off the disk first, so a restart never brings it back. The bytes are left for the collector, not wiped: a
            // backup may be copying them from the ring it read a moment ago.
            FieldKeyRing.Remove(_directories, id);
            Volatile.Write(ref _ring, ring.Without(id, FieldKeyRing.Stamp(_directories)));
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

            // Another node sharing the key folder may have rotated a moment ago: read the ring again before giving up.
            if (!ring.Keys.ContainsKey(id) && _shared && Due(ref _unknownCheckedAt, UnknownKeyCheckInterval))
            {
                ring = ReloadIfChanged(ring);
            }

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

    /// <summary>
    /// The keys by id, the original key's id, and the current key's id. Replaced whole on a rotation. <see cref="Stamp"/> is
    /// how the ring's folder looked when it was read (<see cref="FieldKeyRing.Stamp"/>).
    /// </summary>
    private sealed record FieldKeyRingState(IReadOnlyDictionary<string, byte[]> Keys, string OriginalId, string CurrentId, string Stamp)
    {
        public bool IsOriginalCurrent => CurrentId == OriginalId;

        public static FieldKeyRingState Load(DataDirectories directories, IDataProtector wrapper, byte[] original)
        {
            // Taken first: a change made while the ring is read shows as a change at the next look.
            var stamp = FieldKeyRing.Stamp(directories);
            var originalId = FieldMasterKey.Fingerprint(original);
            var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [originalId] = original };
            foreach (var key in FieldKeyRing.Load(directories, wrapper))
            {
                keys.TryAdd(FieldMasterKey.Fingerprint(key), key);
            }

            var current = FieldKeyRing.Current(directories) ?? originalId;
            return keys.ContainsKey(current)
                ? new FieldKeyRingState(keys, originalId, current, stamp)
                : throw new CryptographicException($"The current field key {current} is missing from {FieldKeyRing.DirectoryIn(directories)}.");
        }

        public FieldKeyRingState With(string id, byte[] key, string stamp) =>
            new(new Dictionary<string, byte[]>(Keys, StringComparer.Ordinal) { [id] = key }, OriginalId, id, stamp);

        public FieldKeyRingState Without(string id, string stamp)
        {
            var keys = new Dictionary<string, byte[]>(Keys, StringComparer.Ordinal);
            keys.Remove(id);
            return new FieldKeyRingState(keys, OriginalId, CurrentId, stamp);
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

    /// <summary>
    /// Why this node may not start on the folder several nodes share (SCAL-01): it has a data key of its own under its data
    /// directory, and the shared folder has none, so it would make a new key there, and nothing encrypted before could be
    /// read. Null when there is no such problem.
    /// </summary>
    public static string? SharedFolderProblem(DataDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (!directories.IsShared)
        {
            return null;
        }

        var own = Path.Combine(directories.Root, "keys", FileName);
        return File.Exists(own) && !File.Exists(PathIn(directories))
            ? $"this server has a data encryption key of its own ({own}), and the shared folder {directories.Keys} has none, so the " +
              "data encrypted with it could not be read. Put the keys in the shared folder first: with the shared folder set, run " +
              "'HyFib.HealthCare360.Server.exe backup restore --in <the latest backup>' (docs/deployment/DEPLOYMENT.md, section 10)."
            : null;
    }

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

        // Another node sharing the key folder (SCAL-01) may make the key at the same moment: the first one written is used.
        var key = RandomNumberGenerator.GetBytes(Length);
        return KeyFiles.CreateNew(path, protector.Protect(key)) ? key : protector.Unprotect(File.ReadAllBytes(path));
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
        var temp = KeyFiles.TempPath(path);
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
            KeyFiles.CreateNew(path, wrapper.Protect(key));
        }

        return id;
    }

    /// <summary>
    /// What changes when the ring does: which keys it holds and which one is current. A node of several compares it to see
    /// whether another node rotated or retired a key (SCAL-01).
    /// </summary>
    public static string Stamp(DataDirectories directories)
    {
        var folder = DirectoryIn(directories);
        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        var keys = Directory.EnumerateFiles(folder, "*" + KeyExtension).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        return string.Join(',', keys) + "|" + Current(directories);
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
        var temp = KeyFiles.TempPath(path);
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
/// <para>
/// With several nodes (<see cref="ClusterOptions.SharedDirectory"/>), the ring is in the shared folder, beside the keys it
/// wraps, and every node must read it: it is protected with the cluster's key certificate instead of machine DPAPI, which
/// only the machine that wrote it can open (SCAL-01). It is never kept in the database, so a copy of the database (a
/// backup, the reporting replica, the warm standby) opens no encrypted value without the shared folder as well. What
/// belongs to one node only, its hospital CA and TLS certificate, stays wrapped by that node's own ring
/// (<see cref="CreateNodeProvider"/>).
/// </para>
/// </summary>
public static class HospitalDataProtection
{
    public const string ApplicationName = "HyFib.HealthCare360";

    public static string KeyRingDirectory(DataDirectories directories) => Path.Combine(directories.Keys, "dataprotection");

    public static IDataProtectionProvider CreateProvider(DataDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        return DataProtectionProvider.Create(new DirectoryInfo(KeyRingDirectory(directories)), protection => Configure(protection, directories));
    }

    /// <summary>
    /// The ring of this machine alone (<c>keys/dataprotection</c> under its own data directory, machine DPAPI on Windows),
    /// for what no other node reads: its hospital CA and TLS certificate. On a single Server Node it is the same ring as
    /// <see cref="CreateProvider"/>.
    /// </summary>
    public static IDataProtectionProvider CreateNodeProvider(DataDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (!directories.IsShared)
        {
            return CreateProvider(directories);
        }

        return DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directories.Root, "keys", "dataprotection")), protection =>
        {
            protection.SetApplicationName(ApplicationName);
            if (OperatingSystem.IsWindows())
            {
                protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
            }
        });
    }

    /// <summary>How keys in the ring are protected at rest: the cluster's certificate when shared, else machine DPAPI on Windows.</summary>
    public static void Configure(IDataProtectionBuilder protection, DataDirectories directories)
    {
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(directories);
        protection.SetApplicationName(ApplicationName);
        if (directories.IsShared)
        {
            // Without a certificate (allowed only in development, PlatformOptionsValidator) the ring is written unprotected,
            // as it is on a single node that is not Windows.
            if (ClusterKeyCertificate.Load(directories.Cluster) is { } certificate)
            {
                protection.ProtectKeysWithCertificate(certificate).UnprotectKeysWithAnyCertificate(certificate);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }
    }
}

/// <summary>
/// The certificate that protects the shared Data Protection key ring of several nodes (<see cref="ClusterOptions"/>): from
/// the Local Computer's Personal store by thumbprint, or from a PFX file. It must hold an RSA private key, which wraps each
/// key of the ring (XML encryption).
/// </summary>
public static class ClusterKeyCertificate
{
    /// <summary>The configured certificate, or null when none is configured.</summary>
    /// <exception cref="InvalidOperationException">It is configured but cannot be found, or cannot protect keys.</exception>
    public static X509Certificate2? Load(ClusterOptions cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        X509Certificate2? certificate = null;
        if (!string.IsNullOrWhiteSpace(cluster.KeyCertificateThumbprint))
        {
            var thumbprint = new string(cluster.KeyCertificateThumbprint.Where(Uri.IsHexDigit).ToArray());
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            certificate = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).FirstOrDefault()
                ?? throw new InvalidOperationException(
                    $"HealthCare360:Cluster:KeyCertificateThumbprint names certificate {thumbprint}, which is not in the Local Computer's Personal store of this machine.");
        }
        else if (!string.IsNullOrWhiteSpace(cluster.KeyCertificatePath))
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cluster.KeyCertificatePath));
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, cluster.KeyCertificatePassword,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
        }

        if (certificate is null)
        {
            return null;
        }

        using var rsa = certificate.GetRSAPrivateKey();
        return rsa is not null
            ? certificate
            : throw new InvalidOperationException(
                $"The cluster key certificate {certificate.Thumbprint} has no RSA private key this server may use, so it cannot protect the shared keys.");
    }
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
