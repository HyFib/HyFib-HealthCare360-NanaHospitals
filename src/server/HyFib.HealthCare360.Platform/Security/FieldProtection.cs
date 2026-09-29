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
/// </summary>
public interface IFieldProtector
{
    /// <summary>Encrypts a short string: <c>v1.&lt;base64url(nonce|tag|ciphertext)&gt;</c>.</summary>
    string Protect(string plaintext);

    string? Unprotect(string? protectedValue);

    /// <summary>
    /// Deterministic keyed hash (HMAC-SHA256) for exact-match lookups without storing plaintext.
    /// Normalise the value (trim, remove spaces, upper-case) before calling.
    /// </summary>
    string BlindIndex(string normalizedValue);

    byte[] ProtectBytes(ReadOnlySpan<byte> plaintext, string purpose);

    byte[] UnprotectBytes(ReadOnlySpan<byte> protectedData, string purpose);

    /// <summary>A 256-bit key dedicated to <paramref name="purpose"/> (for streaming encryption such as backups).</summary>
    byte[] DeriveKey(string purpose);
}

/// <summary>
/// The field master key itself, for the encrypted escrow copy every backup carries (DED-16): without it, encrypted
/// identifiers and documents could not be read on replacement hardware. Only the backup service uses it; never log it.
/// </summary>
internal interface IFieldKeyEscrow
{
    /// <summary>A copy of the 32-byte master key.</summary>
    byte[] ExportMasterKey();

    /// <summary>The key's non-secret fingerprint (<see cref="FieldMasterKey.Fingerprint"/>).</summary>
    string Fingerprint { get; }
}

internal sealed class FieldProtector : IFieldProtector, IFieldKeyEscrow
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Lazy<byte[]> _masterKey;

    public FieldProtector(DataDirectories directories, IDataProtectionProvider dataProtection)
    {
        var path = FieldMasterKey.PathIn(directories);
        var protector = FieldMasterKey.CreateProtector(dataProtection);
        _masterKey = new Lazy<byte[]>(() => FieldMasterKey.LoadOrCreate(path, protector), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public byte[] ExportMasterKey() => (byte[])_masterKey.Value.Clone();

    public string Fingerprint => FieldMasterKey.Fingerprint(_masterKey.Value);

    public string Protect(string plaintext)
    {
        var data = ProtectBytes(Encoding.UTF8.GetBytes(plaintext), "field");
        return "v1." + Base64UrlEncoder.Encode(data);
    }

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return protectedValue;
        }

        if (!protectedValue.StartsWith("v1.", StringComparison.Ordinal))
        {
            throw new CryptographicException("Unsupported protected value format.");
        }

        return Encoding.UTF8.GetString(UnprotectBytes(Base64UrlEncoder.DecodeBytes(protectedValue[3..]), "field"));
    }

    public string BlindIndex(string normalizedValue)
    {
        var key = DeriveKey("blind-index");
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(normalizedValue)));
    }

    public byte[] ProtectBytes(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var key = DeriveKey(purpose);
        var output = new byte[1 + NonceSize + TagSize + plaintext.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        var tag = output.AsSpan(1 + NonceSize, TagSize);
        var cipher = output.AsSpan(1 + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag, Encoding.UTF8.GetBytes(purpose));
        return output;
    }

    public byte[] UnprotectBytes(ReadOnlySpan<byte> protectedData, string purpose)
    {
        if (protectedData.Length < 1 + NonceSize + TagSize || protectedData[0] != Version)
        {
            throw new CryptographicException("Unsupported or corrupt protected payload.");
        }

        var key = DeriveKey(purpose);
        var nonce = protectedData.Slice(1, NonceSize);
        var tag = protectedData.Slice(1 + NonceSize, TagSize);
        var cipher = protectedData[(1 + NonceSize + TagSize)..];
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plaintext, Encoding.UTF8.GetBytes(purpose));
        return plaintext;
    }

    public byte[] DeriveKey(string purpose) => FieldMasterKey.DeriveKey(_masterKey.Value, purpose);
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
        string? keptAs = null;
        if (existing is not null)
        {
            keptAs = $"{path}.replaced-{now.UtcDateTime:yyyyMMdd-HHmmss}";
            File.Move(path, keptAs, overwrite: false);
        }

        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(masterKey));
        File.Move(temp, path, overwrite: false);
        return keptAs is null ? null : Path.GetFileName(keptAs);
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
