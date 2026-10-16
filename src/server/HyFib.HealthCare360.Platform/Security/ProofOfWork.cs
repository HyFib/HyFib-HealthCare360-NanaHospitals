using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>What became of a proof of work sent with a request (<see cref="ProofOfWork.Verify"/>).</summary>
public enum ProofOfWorkOutcome
{
    Accepted = 0,

    /// <summary>No proof was sent.</summary>
    Missing,

    /// <summary>The proof is not of the form <c>{challenge}:{nonce}</c>, or the challenge cannot be read.</summary>
    Malformed,

    /// <summary>The challenge's signature does not match: it was changed, or not issued by this server.</summary>
    Tampered,

    /// <summary>The challenge was issued for another kind of request.</summary>
    WrongAction,

    Expired,

    /// <summary>
    /// The nonce solves the challenge, but the challenge asks for fewer bits than the request now needs: the work asked of
    /// the address or the number went up after it was issued.
    /// </summary>
    TooEasy,

    /// <summary>The nonce does not solve the challenge.</summary>
    Unsolved,

    /// <summary>The challenge was already spent on an earlier request.</summary>
    Replayed,
}

/// <summary>
/// Remembers spent challenges until they expire, so each one is good for a single request. In memory on each server: with
/// several API servers (SCAL-01), a challenge could be spent once on each within its few minutes, which the rate limits
/// still bound (SECURITY.md, "Anti-automation").
/// </summary>
public interface IProofOfWorkReplayCache
{
    /// <summary>Records the challenge as spent; false when it already was.</summary>
    bool TrySpend(string challengeId, DateTimeOffset expiresAt);
}

internal sealed class MemoryProofOfWorkReplayCache(IMemoryCache cache, TimeProvider time) : IProofOfWorkReplayCache
{
    private readonly Lock _gate = new();

    public bool TrySpend(string challengeId, DateTimeOffset expiresAt)
    {
        var key = "pow:spent:" + challengeId;
        lock (_gate)
        {
            if (cache.TryGetValue(key, out _))
            {
                return false;
            }

            // Kept a minute past the challenge's expiry, so a clock step on the server cannot bring it back. Relative to this
            // server's clock, which is the one the expiry was checked against.
            cache.Set(key, true, expiresAt - time.GetUtcNow() + TimeSpan.FromMinutes(1));
            return true;
        }
    }
}

/// <summary>
/// A self-hosted proof of work (Hashcash, in the manner of ALTCHA) against automated use of the portal's anonymous
/// endpoints (SRS SEC-06, ASVS V2.4.1). Nothing leaves the hospital: no third-party CAPTCHA sees the patients' traffic.
/// <para>
/// The server issues a challenge: <c>1.{action}.{bits}.{expires}.{salt}.{signature}</c>, where the salt is 16 random bytes
/// and the signature an HMAC-SHA256 of the rest under a key of this server's (<see cref="ProofOfWorkKey"/>). The browser
/// finds a nonce, a decimal number, for which SHA-256 of the UTF-8 text <c>{challenge}{nonce}</c> starts with
/// <c>bits</c> zero bits, and sends <c>{challenge}:{nonce}</c>. Checking it takes one HMAC and one hash and no database:
/// only the spent challenges are remembered (<see cref="IProofOfWorkReplayCache"/>), until they expire.
/// </para>
/// </summary>
public sealed class ProofOfWork(byte[] key, IProofOfWorkReplayCache spent, TimeProvider time)
{
    public const string Version = "1";

    /// <summary>The longest nonce accepted: 16 decimal digits is far more tries than any difficulty here needs.</summary>
    private const int MaxNonceDigits = 16;

    private const int MaxProofLength = 512;

    /// <summary>A new challenge for <paramref name="action"/>, asking for <paramref name="bits"/> leading zero bits.</summary>
    public string Issue(string action, int bits, DateTimeOffset expiresAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bits, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bits, 32);
        if (action.Length == 0 || action.Any(c => c is not (>= 'a' and <= 'z' or '-')))
        {
            throw new ArgumentException("An action is lower-case letters and hyphens.", nameof(action));
        }

        var body = string.Join(
            '.',
            Version,
            action,
            bits.ToString(CultureInfo.InvariantCulture),
            expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        return body + "." + Sign(body);
    }

    /// <summary>
    /// Checks a proof (<c>{challenge}:{nonce}</c>) for a request of <paramref name="action"/> that needs
    /// <paramref name="requiredBits"/>. An accepted proof is spent: the same challenge is refused from then on.
    /// </summary>
    public ProofOfWorkOutcome Verify(string? proof, string action, int requiredBits)
    {
        if (string.IsNullOrEmpty(proof))
        {
            return ProofOfWorkOutcome.Missing;
        }

        var colon = proof.LastIndexOf(':');
        if (proof.Length > MaxProofLength || colon <= 0)
        {
            return ProofOfWorkOutcome.Malformed;
        }

        var challenge = proof[..colon];
        var nonce = proof[(colon + 1)..];
        var parts = challenge.Split('.');
        if (parts.Length != 6
            || parts[0] != Version
            || nonce.Length is 0 or > MaxNonceDigits
            || !nonce.All(char.IsAsciiDigit)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
            || bits > 32
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return ProofOfWorkOutcome.Malformed;
        }

        // The signature first: nothing else in the challenge is believed before it.
        var body = challenge[..challenge.LastIndexOf('.')];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts[5]), Encoding.ASCII.GetBytes(Sign(body))))
        {
            return ProofOfWorkOutcome.Tampered;
        }

        if (!string.Equals(parts[1], action, StringComparison.Ordinal))
        {
            return ProofOfWorkOutcome.WrongAction;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
        if (expiresAt <= time.GetUtcNow())
        {
            return ProofOfWorkOutcome.Expired;
        }

        if (LeadingZeroBits(SHA256.HashData(Encoding.UTF8.GetBytes(challenge + nonce))) < bits)
        {
            return ProofOfWorkOutcome.Unsolved;
        }

        // After the work is checked, so that only a browser that did the work it was asked for is told it now needs more
        // (AntiAutomation does not count that as a failure).
        if (bits < requiredBits)
        {
            return ProofOfWorkOutcome.TooEasy;
        }

        // Last, so only a proof that is good in every other way uses the challenge up.
        return spent.TrySpend(parts[5], expiresAt) ? ProofOfWorkOutcome.Accepted : ProofOfWorkOutcome.Replayed;
    }

    /// <summary>The leading zero bits of a hash, as the browser counts them.</summary>
    public static int LeadingZeroBits(ReadOnlySpan<byte> hash)
    {
        var bits = 0;
        foreach (var b in hash)
        {
            if (b == 0)
            {
                bits += 8;
                continue;
            }

            return bits + BitOperations.LeadingZeroCount((uint)b) - 24;
        }

        return bits;
    }

    /// <summary>Finds a nonce for a challenge, as the browser does. For the tests; a server never solves its own challenges.</summary>
    public static string Solve(string challenge, int bits)
    {
        Span<byte> hash = stackalloc byte[32];
        var prefix = Encoding.UTF8.GetBytes(challenge);
        var buffer = new byte[prefix.Length + 20];
        prefix.CopyTo(buffer, 0);
        for (ulong nonce = 0; ; nonce++)
        {
            nonce.TryFormat(buffer.AsSpan(prefix.Length), out var written, provider: CultureInfo.InvariantCulture);
            SHA256.HashData(buffer.AsSpan(0, prefix.Length + written), hash);
            if (LeadingZeroBits(hash) >= bits)
            {
                return challenge + ":" + nonce.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private string Sign(string body) => Base64UrlEncoder.Encode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(body)));
}

/// <summary>
/// The key that signs proof-of-work challenges: 32 random bytes made on first start and kept under the data directory
/// (<c>keys/anti-automation.key</c>), wrapped by ASP.NET Core Data Protection (machine DPAPI on Windows), as the token
/// signing key is. Losing it only makes challenges issued before invalid; the browser asks for a new one.
/// </summary>
public static class ProofOfWorkKey
{
    public const string FileName = "anti-automation.key";
    private const string Purpose = "HC360.AntiAutomation.ChallengeKey.v1";

    public static byte[] LoadOrCreate(DataDirectories directories, IDataProtectionProvider dataProtection) =>
        FieldMasterKey.LoadOrCreate(Path.Combine(directories.Keys, FileName), dataProtection.CreateProtector(Purpose));
}
