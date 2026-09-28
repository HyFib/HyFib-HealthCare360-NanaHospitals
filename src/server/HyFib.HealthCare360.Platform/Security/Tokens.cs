using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Platform.Security;

public sealed record AccessTokenSubject(
    Guid UserId,
    string UserName,
    string DisplayName,
    string UserType,
    Guid SessionId,
    Guid? FacilityId,
    Guid? DeviceId,
    Guid? PractitionerId,
    Guid? PatientId,
    IReadOnlyCollection<string> Roles,
    string? Restriction = null);

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// ECDSA P-256 signing key for access tokens. Generated on first start and stored under the data
/// directory, wrapped by ASP.NET Core Data Protection (machine DPAPI on Windows).
/// </summary>
public sealed class SigningKeyStore
{
    private readonly Lazy<ECDsaSecurityKey> _key;

    public SigningKeyStore(DataDirectories directories, IDataProtectionProvider dataProtection)
    {
        var path = Path.Combine(directories.Keys, "jwt-signing.key");
        var protector = dataProtection.CreateProtector("HC360.Jwt.SigningKey.v1");
        _key = new Lazy<ECDsaSecurityKey>(() => LoadOrCreate(path, protector), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ECDsaSecurityKey Key => _key.Value;

    private static ECDsaSecurityKey LoadOrCreate(string path, IDataProtector protector)
    {
        var ecdsa = ECDsa.Create();
        if (File.Exists(path))
        {
            ecdsa.ImportPkcs8PrivateKey(protector.Unprotect(File.ReadAllBytes(path)), out _);
        }
        else
        {
            ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, protector.Protect(ecdsa.ExportPkcs8PrivateKey()));
            File.Move(temp, path, overwrite: false);
        }

        var keyId = Base64UrlEncoder.Encode(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))[..16];
        return new ECDsaSecurityKey(ecdsa) { KeyId = keyId };
    }
}

public sealed class TokenService(SigningKeyStore keys, IOptions<PlatformOptions> options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedToken CreateAccessToken(AccessTokenSubject subject)
    {
        var security = options.Value.Security;
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(security.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [HcClaims.UserId] = subject.UserId.ToString(),
            [HcClaims.UserName] = subject.UserName,
            [HcClaims.DisplayName] = subject.DisplayName,
            [HcClaims.UserType] = subject.UserType,
            [HcClaims.Session] = subject.SessionId.ToString(),
            [HcClaims.Role] = subject.Roles.ToArray(),
        };
        AddIfPresent(claims, HcClaims.Facility, subject.FacilityId);
        AddIfPresent(claims, HcClaims.Device, subject.DeviceId);
        AddIfPresent(claims, HcClaims.Practitioner, subject.PractitionerId);
        AddIfPresent(claims, HcClaims.Patient, subject.PatientId);
        if (subject.Restriction is { } restriction)
        {
            claims[HcClaims.Restriction] = restriction;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = security.Issuer,
            Audience = security.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.EcdsaSha256),
        };

        return new IssuedToken(_handler.CreateToken(descriptor), expires);
    }

    private static void AddIfPresent(Dictionary<string, object> claims, string name, Guid? value)
    {
        if (value is { } v)
        {
            claims[name] = v.ToString();
        }
    }
}

/// <summary>Opaque secrets (refresh tokens, enrolment codes, OTPs) and their stored hashes.</summary>
public static class SecretTokens
{
    public static string NewOpaqueToken(int bytes = 32) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>SHA-256 hex digest; secrets are only ever stored hashed.</summary>
    public static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    public static string NewNumericCode(int digits)
    {
        var builder = new StringBuilder(digits);
        for (var i = 0; i < digits; i++)
        {
            builder.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
        }

        return builder.ToString();
    }

    /// <summary>Human-friendly code without ambiguous characters (0/O, 1/I/L), e.g. for device enrolment.</summary>
    public static string NewReadableCode(int length)
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        return string.Create(length, alphabet, static (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
            }
        });
    }

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
