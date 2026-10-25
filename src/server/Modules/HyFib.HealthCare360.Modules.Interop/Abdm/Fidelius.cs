using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// One side's key material for a Fidelius transfer: the private key (kept only for the transfer), the public key as
/// ABDM sends it, and the nonce.
/// </summary>
internal sealed record FideliusKeyPair(byte[] PrivateKey, string PublicKey, string Nonce);

/// <summary>
/// Fidelius, the encryption of health information between a HIP and a HIU in ABDM, as NHA's reference implementation
/// (<c>mgrmtech/fidelius-cli</c>, BouncyCastle) does it, which is what the gateway's other parties run:
/// <list type="number">
/// <item>ECDH on BouncyCastle's <c>curve25519</c>: Curve25519 in short Weierstrass form (Wei25519), <b>not</b> RFC 7748
/// X25519. Private keys are plain scalars (no clamping); public keys are uncompressed points, sent either raw (65 bytes,
/// 88 base64 characters) or as an X.509 SubjectPublicKeyInfo with the curve's parameters spelled out. The shared secret is
/// the x-coordinate of the product, 32 bytes.</item>
/// <item>The two 32-byte nonces are XORed: the first 20 bytes are the HKDF salt, the last 12 the AES-GCM IV.</item>
/// <item>The AES-256 key is HKDF-SHA256 (no info) of the shared secret under that salt.</item>
/// <item>AES-256-GCM with a 128-bit tag appended to the ciphertext, base64.</item>
/// </list>
/// The document is encrypted as UTF-8. The reference encrypts with the JVM's default charset, which is UTF-8 from Java
/// 18 on but not always before; a HIU on an older JVM may misread non-English text.
/// </summary>
internal static class Fidelius
{
    public const string CryptoAlgorithm = "ECDH";
    public const string Curve = "Curve25519";

    /// <summary>The <c>parameters</c> ABDM puts beside the key value.</summary>
    public const string KeyParameters = "Curve25519/32byte random key";

    private const int PointLength = 65;

    /// <summary>Curve25519 in short Weierstrass form, exactly as BouncyCastle's <c>CustomNamedCurves.getByName("curve25519")</c>.</summary>
    private static readonly ECCurve Wei25519 = new()
    {
        CurveType = ECCurve.ECCurveType.PrimeShortWeierstrass,
        Prime = Convert.FromHexString("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffed"),
        A = Convert.FromHexString("2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa984914a144"),
        B = Convert.FromHexString("7b425ed097b425ed097b425ed097b425ed097b425ed097b4260b5e9c7710c864"),
        G = new ECPoint
        {
            X = Convert.FromHexString("2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaad245a"),
            Y = Convert.FromHexString("20ae19a1b8a086b4e01edd2c7748d14c923d4d7e6d7c61b229e9c5a27eced3d9"),
        },
        Order = Convert.FromHexString("1000000000000000000000000000000014def9dea2f79cd65812631a5cf5d3ed"),
        Cofactor = [8],
    };

    /// <summary>
    /// The DER of an X.509 SubjectPublicKeyInfo for this curve up to the point: the id-ecPublicKey algorithm with the curve's
    /// parameters spelled out, then the BIT STRING header. BouncyCastle writes every Fidelius public key so.
    /// </summary>
    private static readonly byte[] X509Prefix = Convert.FromBase64String(
        "MIIBMTCB6gYHKoZIzj0CATCB3gIBATArBgcqhkjOPQEBAiB/////////////////////////////////////////7TBEBCAqqqqqqqqqqqqqqqqqqqqqqqqqq"
        + "qqqqqqqqqqYSRShRAQge0Je0Je0Je0Je0Je0Je0Je0Je0Je0Je0JgtenHcQyGQEQQQqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq0kWiCuGaG4oIa04B7dLH"
        + "dI0UySPU1+bXxhsinpxaJ+ztPZAiAQAAAAAAAAAAAAAAAAAAAAFN753qL3nNZYEmMaXPXT7QIBCANCAA==");

    /// <summary>A fresh key pair and nonce for one transfer. The public key is X.509-encoded, as the reference's HIUs send theirs.</summary>
    public static FideliusKeyPair Generate()
    {
        using var ecdh = ECDiffieHellman.Create(Wei25519);
        var parameters = ecdh.ExportParameters(includePrivateParameters: true);
        return new FideliusKeyPair(Pad(parameters.D!), Convert.ToBase64String([.. X509Prefix, .. Point(parameters.Q)]), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>Encrypts <paramref name="plainText"/> from the sender to the requester.</summary>
    public static Result<string> Encrypt(string plainText, byte[] senderPrivateKey, string senderNonce, string requesterPublicKey, string requesterNonce)
    {
        var key = DeriveKey(senderPrivateKey, requesterPublicKey, senderNonce, requesterNonce);
        if (key.IsFailure)
        {
            return key.Error!;
        }

        var (aesKey, iv) = key.Value;
        var plain = Encoding.UTF8.GetBytes(plainText);
        var output = new byte[plain.Length + 16];
        using var gcm = new AesGcm(aesKey, 16);
        gcm.Encrypt(iv, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length));
        return Convert.ToBase64String(output);
    }

    /// <summary>Decrypts what the sender encrypted for the requester; refused when the data was altered or the keys do not match.</summary>
    public static Result<string> Decrypt(string encrypted, byte[] requesterPrivateKey, string requesterNonce, string senderPublicKey, string senderNonce)
    {
        var key = DeriveKey(requesterPrivateKey, senderPublicKey, senderNonce, requesterNonce);
        if (key.IsFailure)
        {
            return key.Error!;
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(encrypted);
        }
        catch (FormatException)
        {
            return Error.Rule("abdm.fidelius", "The encrypted data is not base64.");
        }

        if (data.Length < 16)
        {
            return Error.Rule("abdm.fidelius", "The encrypted data is too short to carry its authentication tag.");
        }

        var (aesKey, iv) = key.Value;
        var plain = new byte[data.Length - 16];
        try
        {
            using var gcm = new AesGcm(aesKey, 16);
            gcm.Decrypt(iv, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return Error.Rule("abdm.fidelius", "The encrypted data does not decrypt with these keys: it was altered, or the keys or nonces do not match.");
        }

        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// The private key as BouncyCastle writes it (a signed big-endian integer, so 31 to 33 bytes) made into the 32
    /// bytes .NET expects.
    /// </summary>
    public static byte[] Pad(byte[] scalar)
    {
        var trimmed = scalar.AsSpan().TrimStart((byte)0);
        if (trimmed.Length > 32)
        {
            throw new ArgumentException("A Curve25519 private key is at most 32 bytes.", nameof(scalar));
        }

        var padded = new byte[32];
        trimmed.CopyTo(padded.AsSpan(32 - trimmed.Length));
        return padded;
    }

    private static Result<(byte[] Key, byte[] Iv)> DeriveKey(byte[] privateKey, string publicKey, string senderNonce, string requesterNonce)
    {
        byte[] sender, requester;
        try
        {
            sender = Convert.FromBase64String(senderNonce);
            requester = Convert.FromBase64String(requesterNonce);
        }
        catch (FormatException)
        {
            return Error.Rule("abdm.fidelius", "A nonce is not base64.");
        }

        if (sender.Length != 32 || requester.Length != 32)
        {
            return Error.Rule("abdm.fidelius", "Each nonce must be 32 bytes.");
        }

        var point = ReadPublicKey(publicKey);
        if (point.IsFailure)
        {
            return point.Error!;
        }

        byte[] secret;
        try
        {
            using var mine = ECDiffieHellman.Create(new ECParameters { Curve = Wei25519, D = Pad(privateKey) });
            using var theirs = ECDiffieHellman.Create(new ECParameters
            {
                Curve = Wei25519,
                Q = new ECPoint { X = point.Value[1..33], Y = point.Value[33..] },
            });
            secret = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        }
        catch (CryptographicException)
        {
            return Error.Rule("abdm.fidelius", "The public key is not a point on Curve25519.");
        }

        var xor = new byte[32];
        for (var i = 0; i < 32; i++)
        {
            xor[i] = (byte)(sender[i] ^ requester[i]);
        }

        var aesKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: xor[..20]);
        return (aesKey, xor[^12..]);
    }

    /// <summary>The uncompressed point of a public key sent raw or X.509-encoded for this curve; anything else is refused.</summary>
    private static Result<byte[]> ReadPublicKey(string publicKey)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(publicKey);
        }
        catch (FormatException)
        {
            return Error.Rule("abdm.fidelius", "The public key is not base64.");
        }

        if (bytes.Length == X509Prefix.Length + PointLength && bytes.AsSpan(0, X509Prefix.Length).SequenceEqual(X509Prefix))
        {
            bytes = bytes[X509Prefix.Length..];
        }

        return bytes.Length == PointLength && bytes[0] == 0x04
            ? bytes
            : Error.Rule("abdm.fidelius", "The public key is neither an uncompressed Curve25519 point nor its X.509 encoding.");
    }

    private static byte[] Point(ECPoint q) => [0x04, .. Pad(q.X!), .. Pad(q.Y!)];
}

/// <summary>
/// The HIP's side of a transfer: one fresh key pair for the transfer, and each document encrypted with it for the HIU's
/// key material. The checksum is the MD5 of the document, as ABDM's data push asks; it identifies, it does not protect
/// (AES-GCM's tag does).
/// </summary>
internal sealed class FideliusCipher : IFideliusCipher
{
    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Primitives",
        Justification = "ABDM's data push names each entry by the MD5 of its document; integrity comes from AES-GCM's tag.")]
    public Result<(IReadOnlyList<AbdmEncryptedEntry> Entries, AbdmKeyMaterial HipKeyMaterial)> Encrypt(
        IReadOnlyList<(string CareContextReference, string DocumentJson)> documents, AbdmKeyMaterial hiu)
    {
        if (!string.Equals(hiu.CryptoAlgorithm, Fidelius.CryptoAlgorithm, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hiu.Curve, Fidelius.Curve, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Rule("abdm.fidelius", $"The HIU asked for {hiu.CryptoAlgorithm} on {hiu.Curve}; ABDM uses ECDH on Curve25519.");
        }

        var hip = Fidelius.Generate();
        var entries = new List<AbdmEncryptedEntry>(documents.Count);
        foreach (var (reference, json) in documents)
        {
            var encrypted = Fidelius.Encrypt(json, hip.PrivateKey, hip.Nonce, hiu.PublicKey, hiu.Nonce);
            if (encrypted.IsFailure)
            {
                return encrypted.Error!;
            }

            entries.Add(new AbdmEncryptedEntry(reference, encrypted.Value, Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(json)))));
        }

        return (entries, new AbdmKeyMaterial(Fidelius.CryptoAlgorithm, Fidelius.Curve, hip.PublicKey, hip.Nonce));
    }
}
