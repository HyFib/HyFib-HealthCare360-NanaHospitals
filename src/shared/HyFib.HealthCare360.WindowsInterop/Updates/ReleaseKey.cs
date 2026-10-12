using System.Security.Cryptography;

namespace HyFib.HealthCare360.WindowsInterop.Updates;

/// <summary>
/// The public half of a HyFib release signing key: an ECDSA key on the P-256 curve, which verifies SHA-256 signatures
/// in the IEEE P1363 format (r and s, 32 bytes each). Its id is the first 8 bytes of the SHA-256 of its
/// SubjectPublicKeyInfo, in lower-case hex: the signature file names the key that made it.
/// </summary>
public sealed class ReleaseKey
{
    /// <summary>A P-256 public key's SubjectPublicKeyInfo up to the point (id-ecPublicKey, prime256v1, BIT STRING).</summary>
    private static readonly byte[] P256Prefix = Convert.FromHexString("3059301306072A8648CE3D020106082A8648CE3D030107034200");

    private const int P256Length = 91;

    private readonly byte[] _subjectPublicKeyInfo;

    private ReleaseKey(byte[] subjectPublicKeyInfo)
    {
        _subjectPublicKeyInfo = subjectPublicKeyInfo;
        Id = IdOf(subjectPublicKeyInfo);
    }

    public string Id { get; }

    /// <summary>The key from its SubjectPublicKeyInfo, DER-encoded; only an uncompressed P-256 point is accepted.</summary>
    public static ReleaseKey FromSubjectPublicKeyInfo(ReadOnlySpan<byte> subjectPublicKeyInfo)
    {
        if (subjectPublicKeyInfo.Length != P256Length || !subjectPublicKeyInfo.StartsWith(P256Prefix) || subjectPublicKeyInfo[P256Prefix.Length] != 0x04)
        {
            throw new ArgumentException("A release key must be an ECDSA P-256 public key (an uncompressed point).", nameof(subjectPublicKeyInfo));
        }

        // The point must be on the curve: ImportSubjectPublicKeyInfo checks it.
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return new ReleaseKey(subjectPublicKeyInfo.ToArray());
    }

    /// <summary>The key from its <c>-----BEGIN PUBLIC KEY-----</c> PEM, as the signing script prints it.</summary>
    public static ReleaseKey FromPem(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);
        var fields = PemEncoding.Find(pem);
        if (!pem.AsSpan()[fields.Label].SequenceEqual("PUBLIC KEY"))
        {
            throw new ArgumentException("A release key is given as a PUBLIC KEY PEM.", nameof(pem));
        }

        return FromSubjectPublicKeyInfo(Convert.FromBase64String(pem[fields.Base64Data]));
    }

    public static string IdOf(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo)[..8]);

    /// <summary>Whether <paramref name="signature"/> (IEEE P1363) is this key's signature of <paramref name="data"/>.</summary>
    public bool Verifies(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != 64)
        {
            return false;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(_subjectPublicKeyInfo, out _);
        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public override string ToString() => Id;
}
