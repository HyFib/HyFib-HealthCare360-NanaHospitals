using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>
/// Unguessable URL tokens for anonymous links (QR feedback forms, personal feedback links, opt-out links): 128 random
/// bits, URL-safe. Personal tokens are stored only as SHA-256 hashes and looked up by hash.
/// </summary>
public static class OpaqueToken
{
    public const int MinLength = 16;
    public const int MaxLength = 64;

    public static string New() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Rejects anything that is not a plausible token before it reaches the database.</summary>
    public static bool IsWellFormed(string? token) =>
        token is { Length: >= MinLength and <= MaxLength } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
