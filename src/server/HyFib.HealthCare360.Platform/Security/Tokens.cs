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
/// <para>
/// With several nodes the files are in the folder they share (SCAL-01), and a rotation on one node reaches the others: each
/// reads the key file again when it has changed, looking at most every <see cref="SharedCheckInterval"/>, and at once
/// (at most once a second) when a token names a key it does not know.
/// </para>
/// </summary>
public sealed class SigningKeyStore
{
    /// <summary>How often a node of several looks whether another one rotated the key.</summary>
    public static readonly TimeSpan SharedCheckInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan UnknownKeyCheckInterval = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly string _previousPath;
    private readonly string _retiredPath;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _time;
    private readonly TimeSpan _overlap;
    private readonly bool _shared;
    private readonly Lock _rotation = new();
    private readonly Lazy<SigningKeys> _initial;
    private SigningKeys? _keys;
    private long _checkedAt;
    private long _unknownCheckedAt;

    public SigningKeyStore(DataDirectories directories, IDataProtectionProvider dataProtection, IOptions<PlatformOptions> options, TimeProvider time)
    {
        _path = Path.Combine(directories.Keys, "jwt-signing.key");
        _previousPath = Path.Combine(directories.Keys, "jwt-signing.previous.key");
        _retiredPath = Path.Combine(directories.Keys, "jwt-signing.previous.retired");
        _protector = dataProtection.CreateProtector("HC360.Jwt.SigningKey.v1");
        _time = time;
        _shared = directories.IsShared;

        // An access token signed just before a rotation stays valid until it expires, give or take the clock skew.
        _overlap = TimeSpan.FromMinutes(options.Value.Security.AccessTokenMinutes) + TimeSpan.FromMinutes(1);
        _initial = new Lazy<SigningKeys>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private SigningKeys Current
    {
        get
        {
            var keys = Volatile.Read(ref _keys) ?? _initial.Value;
            return _shared && Due(ref _checkedAt, SharedCheckInterval) ? ReloadIfChanged(keys) : keys;
        }
    }

    /// <summary>The key new access tokens are signed with.</summary>
    public ECDsaSecurityKey Key => Current.Key;

    /// <summary>When the current key was made (its file was written).</summary>
    public DateTimeOffset? KeyCreatedAt => File.Exists(_path) ? new DateTimeOffset(File.GetLastWriteTimeUtc(_path), TimeSpan.Zero) : null;

    /// <summary>The keys an access token may be signed with: the current one, and the previous one shortly after a rotation.</summary>
    public IEnumerable<SecurityKey> ValidationKeys() => ValidationKeys(keyId: null);

    /// <summary>
    /// As <see cref="ValidationKeys()"/>, for a token signed with key <paramref name="keyId"/>. A node of several that does
    /// not know that key reads the key files again first: another node may have rotated it a moment ago.
    /// </summary>
    public IEnumerable<SecurityKey> ValidationKeys(string? keyId)
    {
        var keys = Current;
        if (_shared && keyId is not null && keyId != keys.Key.KeyId && keyId != keys.Previous?.KeyId && Due(ref _unknownCheckedAt, UnknownKeyCheckInterval))
        {
            keys = ReloadIfChanged(keys);
        }

        return keys.Previous is { } previous && keys.RetiredAt is { } retired && _time.GetUtcNow() < retired + _overlap
            ? [keys.Key, previous]
            : [keys.Key];
    }

    /// <summary>Reads the key files again now when another node changed them (before a rotation here, for one).</summary>
    public void Refresh()
    {
        if (_shared)
        {
            ReloadIfChanged(Volatile.Read(ref _keys) ?? _initial.Value);
        }
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
        Refresh();
        lock (_rotation)
        {
            var keys = Volatile.Read(ref _keys) ?? _initial.Value;
            var now = _time.GetUtcNow();
            if (keys.RetiredAt is { } retired && now < retired + _overlap)
            {
                throw new InvalidOperationException($"The signing key was rotated a moment ago; it can be rotated again from {retired + _overlap:O}.");
            }

            // The current key is kept as the previous one before the new one takes its place.
            var currentFile = File.ReadAllBytes(_path);
            WriteReplacing(_previousPath, currentFile);
            WriteReplacing(_retiredPath, System.Text.Encoding.UTF8.GetBytes(now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));

            var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            WriteReplacing(_path, _protector.Protect(ecdsa.ExportPkcs8PrivateKey()));

            // The files as this node just wrote them: rotations take the cluster's key lock (KeyRotationService), so no other
            // node writes them meanwhile.
            var next = new SigningKeys(ToSecurityKey(ecdsa), keys.Key, now, Stamp());
            Volatile.Write(ref _keys, next);
            return next.Key.KeyId;
        }
    }

    private SigningKeys Load()
    {
        // Taken first: a rotation another node makes while the files are read shows as a change at the next look. Taken
        // last, it could match files written after the key was read, and this node would keep the old key for good.
        var stamp = Stamp();
        var ecdsa = ECDsa.Create();
        if (File.Exists(_path))
        {
            ecdsa.ImportPkcs8PrivateKey(_protector.Unprotect(File.ReadAllBytes(_path)), out _);
        }
        else
        {
            ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
            if (!KeyFiles.CreateNew(_path, _protector.Protect(ecdsa.ExportPkcs8PrivateKey())))
            {
                // Another node sharing the folder made the key first: use that one.
                ecdsa.ImportPkcs8PrivateKey(_protector.Unprotect(File.ReadAllBytes(_path)), out _);
            }
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

        return new SigningKeys(ToSecurityKey(ecdsa), previous, retiredAt, stamp);
    }

    // A rotation rewrites the key file and the time the previous key was retired: either changing means another node rotated.
    private (DateTime Key, DateTime Retired) Stamp() => (File.GetLastWriteTimeUtc(_path), File.GetLastWriteTimeUtc(_retiredPath));

    /// <summary>
    /// The keys as they are now in the shared folder, when another node changed them since they were read. A folder that
    /// cannot be read at the moment, or a rotation half written, leaves the keys as they were until the next look.
    /// </summary>
    private SigningKeys ReloadIfChanged(SigningKeys keys)
    {
        lock (_rotation)
        {
            var current = Volatile.Read(ref _keys) ?? keys;
            try
            {
                if (!File.Exists(_path) || Stamp() == current.Stamp)
                {
                    return current;
                }

                var reloaded = Load();
                Volatile.Write(ref _keys, reloaded);
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

    private static ECDsaSecurityKey ToSecurityKey(ECDsa ecdsa) =>
        new(ecdsa) { KeyId = Base64UrlEncoder.Encode(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))[..16] };

    private static void WriteReplacing(string path, byte[] content)
    {
        var temp = KeyFiles.TempPath(path);
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private sealed record SigningKeys(ECDsaSecurityKey Key, ECDsaSecurityKey? Previous, DateTimeOffset? RetiredAt, (DateTime Key, DateTime Retired) Stamp);
}

/// <summary>Writing key files that several nodes may write at the same moment, in the folder they share (SCAL-01).</summary>
internal static class KeyFiles
{
    /// <summary>A temporary name of the writer's own beside <paramref name="path"/>, which no other node writes to.</summary>
    public static string TempPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

    /// <summary>
    /// Writes a new key file, complete or not at all. False when the file was there already, because another node sharing
    /// the folder made it first: the caller uses that one.
    /// </summary>
    public static bool CreateNew(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = TempPath(path);
        File.WriteAllBytes(temp, content);
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return false;
        }
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
