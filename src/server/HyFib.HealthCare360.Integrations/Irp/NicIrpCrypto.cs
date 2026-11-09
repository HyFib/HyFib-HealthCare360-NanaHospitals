using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Irp;

/// <summary>
/// NIC's e-invoice API encryption: at authentication a random 32-byte app key travels inside the request, which is
/// encrypted with NIC's RSA public key (PKCS#1 v1.5); the answer's session key (<c>Sek</c>) is encrypted with the app key
/// (AES-256, ECB, PKCS#7), and every later request and answer payload with the session key, all in base64.
/// </summary>
internal static class NicIrpCrypto
{
    public static byte[] NewAppKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The authentication request's <c>Data</c>: its JSON under NIC's public key.</summary>
    public static string EncryptWithPublicKey(string json, string publicKeyPem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(json), RSAEncryptionPadding.Pkcs1));
    }

    /// <summary>The session key from the authentication answer's <c>Sek</c>.</summary>
    public static byte[] DecryptSessionKey(string sek, byte[] appKey) => Decrypt(Convert.FromBase64String(sek), appKey);

    public static string EncryptPayload(string json, byte[] sessionKey) => Convert.ToBase64String(Encrypt(Encoding.UTF8.GetBytes(json), sessionKey));

    public static string DecryptPayload(string data, byte[] sessionKey) => Encoding.UTF8.GetString(Decrypt(Convert.FromBase64String(data), sessionKey));

    public static byte[] Encrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptEcb(data, PaddingMode.PKCS7);
    }

    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptEcb(data, PaddingMode.PKCS7);
    }
}
