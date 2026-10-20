using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>What became of a proof of work sent with a request (<see cref="ProofOfWork.VerifyAsync"/>).</summary>
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
/// A self-hosted proof of work (Hashcash, in the manner of ALTCHA) against automated use of the portal's anonymous
/// endpoints (SRS SEC-06, ASVS V2.4.1). Nothing leaves the hospital: no third-party CAPTCHA sees the patients' traffic.
/// <para>
/// The server issues a challenge: <c>1.{action}.{bits}.{expires}.{salt}.{signature}</c>, where the salt is 16 random bytes
/// and the signature an HMAC-SHA256 of the rest under a key of this server's (<see cref="ProofOfWorkKey"/>). The browser
/// finds a nonce, a decimal number, for which SHA-256 of the UTF-8 text <c>{challenge}{nonce}</c> starts with
/// <c>bits</c> zero bits, and sends <c>{challenge}:{nonce}</c>. Checking it takes one HMAC and one hash; only the spent
/// challenges are remembered, until they expire, in <paramref name="spent"/>: the cluster store on a node of several, so a
/// challenge is spent once for the hospital whichever node it is sent to (SCAL-01), and the server's memory on a single
/// Server Node (<see cref="MemoryClusterStore"/>).
/// </para>
/// </summary>
public sealed class ProofOfWork(byte[] key, IClusterStore spent, TimeProvider time)
{
    public const string Version = "1";

    /// <summary>What a spent challenge is kept under, with its signature: unique to it, as its salt is.</summary>
    internal const string SpentPrefix = "pow:spent:";

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
    public async Task<ProofOfWorkOutcome> VerifyAsync(string? proof, string action, int requiredBits, CancellationToken cancellationToken)
    {
        var outcome = Check(proof, action, requiredBits, out var signature, out var expiresAt);
        if (outcome != ProofOfWorkOutcome.Accepted)
        {
            return outcome;
        }

        // Last, so only a proof that is good in every other way uses the challenge up. Kept a minute past the challenge's
        // expiry, so a clock step on a server cannot bring it back.
        var lifetime = expiresAt - time.GetUtcNow() + TimeSpan.FromMinutes(1);
        return await spent.TryAddAsync(SpentPrefix + signature, lifetime, cancellationToken) ? ProofOfWorkOutcome.Accepted : ProofOfWorkOutcome.Replayed;
    }

    /// <summary>Everything <see cref="VerifyAsync"/> checks but whether the challenge was spent: no store is asked.</summary>
    private ProofOfWorkOutcome Check(string? proof, string action, int requiredBits, out string signature, out DateTimeOffset expiresAt)
    {
        signature = string.Empty;
        expiresAt = default;
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

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
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

        signature = parts[5];
        return ProofOfWorkOutcome.Accepted;
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

/// <summary>This server's anti-automation key (<see cref="ProofOfWorkKey"/>), read once for the services that use it.</summary>
internal sealed record AntiAutomationKey(byte[] Value);

/// <summary>
/// The key that signs proof-of-work challenges, and from which the keys of the failure counts' address and number hashes
/// are derived (<see cref="AutomationSuspicion"/>): 32 random bytes made on first start and kept in the keys folder
/// (<c>keys/anti-automation.key</c>, the shared folder's on a node of several, so every node signs and hashes alike),
/// wrapped by ASP.NET Core Data Protection, as the token signing key is. Losing it only makes challenges issued before
/// invalid, and forgets the failures counted so far; the browser asks for a new challenge.
/// </summary>
public static class ProofOfWorkKey
{
    public const string FileName = "anti-automation.key";
    private const string Purpose = "HC360.AntiAutomation.ChallengeKey.v1";

    public static byte[] LoadOrCreate(DataDirectories directories, IDataProtectionProvider dataProtection) =>
        FieldMasterKey.LoadOrCreate(Path.Combine(directories.Keys, FileName), dataProtection.CreateProtector(Purpose));
}
