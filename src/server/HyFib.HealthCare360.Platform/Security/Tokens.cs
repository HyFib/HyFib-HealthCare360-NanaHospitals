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
/// directory (<c>keys/jwt-signing.key</c>), wrapped by ASP.NET Core Data Protection (machine DPAPI on Windows).
/// <para>
/// Rotated from System health (SRS SEC-07): a new key signs from then on, and the previous one
/// (<c>jwt-signing.previous.key</c>) still validates for as long as an access token lives, so nobody is signed out.
/// Refresh tokens are opaque and do not depend on the key.
/// </para>
/// </summary>
public sealed class SigningKeyStore
{
    private readonly string _path;
    private readonly string _previousPath;
    private readonly string _retiredPath;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly TimeSpan _overlap;
    private readonly Lock _rotation = new();
    private readonly Lazy<SigningKeys> _initial;
    private SigningKeys? _keys;

    public SigningKeyStore(DataDirectories directories, IDataProtectionProvider dataProtection, IOptions<PlatformOptions> options, TimeProvider time)
    {
        _path = Path.Combine(directories.Keys, "jwt-signing.key");
        _previousPath = Path.Combine(directories.Keys, "jwt-signing.previous.key");
        _retiredPath = Path.Combine(directories.Keys, "jwt-signing.previous.retired");
        _protector = dataProtection.CreateProtector("HC360.Jwt.SigningKey.v1");
        _time = time;

        // An access token signed just before a rotation stays valid until it expires, give or take the clock skew.
        _overlap = TimeSpan.FromMinutes(options.Value.Security.AccessTokenMinutes) + TimeSpan.FromMinutes(1);
        _initial = new Lazy<SigningKeys>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private SigningKeys Current => Volatile.Read(ref _keys) ?? _initial.Value;

    /// <summary>The key new access tokens are signed with.</summary>
    public ECDsaSecurityKey Key => Current.Key;

    /// <summary>When the current key was made (its file was written).</summary>
    public DateTimeOffset? KeyCreatedAt => File.Exists(_path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(_path), TimeSpan.Zero) : null;

    /// <summary>The keys an access token may be signed with: the current one, and the previous one shortly after a rotation.</summary>
    public IEnumerable<SecurityKey> ValidationKeys()
    {
        var keys = Current;
        return keys.Previous is { } previous && keys.RetiredAt is { } retired && _time.GetUtcNow() < retired + _overlap
            ? [keys.Key, previous]
            : [keys.Key];
    }

    /// <summary>
    /// While the previous key still validates tokens (after a rotation), when the next rotation may be made; otherwise null.
    /// Only one previous key is kept, so a second rotation sooner would end its tokens early.
    /// </summary>
    public DateTimeOffset? NextRotationAt
    {
        get
        {
            var keys = Current;
            return keys.RetiredAt is { } retired && _time.GetUtcNow() < retired + _overlap ? retired + _overlap : null;
        }
    }

    /// <summary>Makes a new key current; the previous one validates tokens it signed until they expire. Returns the new key's id.</summary>
    /// <exception cref="InvalidOperationException">The key was rotated less than an access-token lifetime ago (<see cref="NextRotationAt"/>).</exception>
    public string Rotate()
    {
        lock (_rotation)
        {
            var keys = Current;
            var now = _time.GetUtcNow();
            if (NextRotationAt is { } allowedAt)
            {
                throw new InvalidOperationException($"The signing key was rotated a moment ago; it can be rotated again from {allowedAt:O}.");
            }

            // The current key is kept as the previous one before the new one takes its place.
            var currentFile = File.ReadAllBytes(_path);
            WriteReplacing(_previousPath, currentFile);
            WriteReplacing(_retiredPath, System.Text.Encoding.UTF8.GetBytes(now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            WriteReplacing(_path, _protector.Protect(ecdsa.ExportPkcs8PrivateKey()));
            var next = new SigningKeys(ToSecurityKey(ecdsa), keys.Key, now);
            Volatile.Write(ref _keys, next);
            return next.Key.KeyId;
        }
    }

    private SigningKeys Load()
    {
        var ecdsa = ECDsa.Create();
        if (File.Exists(_path))
        {
            ecdsa.ImportPkcs8PrivateKey(_protector.Unprotect(File.ReadAllBytes(_path)), out _);
        }
        else
        {
            ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, _protector.Protect(ecdsa.ExportPkcs8PrivateKey()));
            File.Move(temp, _path, overwrite: false);
        }

        ECDsaSecurityKey? previous = null;
        DateTimeOffset? retiredAt = null;
        if (File.Exists(_previousPath) && File.Exists(_retiredPath)
            && DateTimeOffset.TryParse(File.ReadAllText(_retiredPath), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var retired))
        {
            var old = ECDsa.Create();
            old.ImportPkcs8PrivateKey(_protector.Unprotect(File.ReadAllBytes(_previousPath)), out _);
            previous = ToSecurityKey(old);
            retiredAt = retired;
        }

        return new SigningKeys(ToSecurityKey(ecdsa), previous, retiredAt);
    }

    private static ECDsaSecurityKey ToSecurityKey(ECDsa ecdsa) =>
        new(ecdsa) { KeyId = Base64UrlEncoder.Encode(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))[..16] };

    private static void WriteReplacing(string path, byte[] content)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record SigningKeys(ECDsaSecurityKey Key, ECDsaSecurityKey? Previous, DateTimeOffset? RetiredAt);
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
