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

internal sealed class FieldProtector : IFieldProtector
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly Lazy<byte[]> _masterKey;

    public FieldProtector(DataDirectories directories, IDataProtectionProvider dataProtection)
    {
        var path = Path.Combine(directories.Keys, "field-master.key");
        var protector = dataProtection.CreateProtector("HC360.FieldProtection.MasterKey.v1");
        _masterKey = new Lazy<byte[]>(() => LoadOrCreate(path, protector), LazyThreadSafetyMode.ExecutionAndPublication);
    }

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

    public byte[] DeriveKey(string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _masterKey.Value, 32, info: Encoding.UTF8.GetBytes("hc360:" + purpose));

    private static byte[] LoadOrCreate(string path, IDataProtector protector)
    {
        if (File.Exists(path))
        {
            return protector.Unprotect(File.ReadAllBytes(path));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, protector.Protect(key));
        File.Move(temp, path, overwrite: false);
        return key;
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
