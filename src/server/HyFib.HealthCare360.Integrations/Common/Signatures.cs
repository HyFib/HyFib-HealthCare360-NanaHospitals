using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Common;

/// <summary>
/// Signature helpers for webhooks and payment checkouts. Every comparison is constant-time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/>) so response timing never reveals how much of a forged
/// signature was correct.
/// </summary>
internal static class Signatures
{
    private const string TwilioMandatesSha1 = "Twilio signs its webhooks with HMAC-SHA1; the algorithm is the provider's, not ours.";

    public static byte[] HmacSha256(string secret, ReadOnlySpan<byte> payload) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);

    public static string HmacSha256Hex(string secret, string payload) =>
        Convert.ToHexStringLower(HmacSha256(secret, Encoding.UTF8.GetBytes(payload)));

    /// <summary>True when <paramref name="providedHex"/> is the hex HMAC-SHA256 of <paramref name="payload"/> (any case).</summary>
    public static bool VerifyHmacSha256Hex(string? secret, ReadOnlySpan<byte> payload, string? providedHex)
    {
        if (string.IsNullOrEmpty(secret) || !TryFromHex(providedHex, out var provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HmacSha256(secret, payload), provided);
    }

    public static bool VerifyHmacSha256Hex(string? secret, string payload, string? providedHex) =>
        VerifyHmacSha256Hex(secret, Encoding.UTF8.GetBytes(payload), providedHex);

    /// <summary>Base64 HMAC-SHA1 — the algorithm Twilio mandates for <c>X-Twilio-Signature</c>.</summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = TwilioMandatesSha1)]
    public static string HmacSha1Base64(string secret, string payload) =>
        Convert.ToBase64String(HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = TwilioMandatesSha1)]
    public static bool VerifyHmacSha1Base64(string? secret, string payload, string? providedBase64)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(providedBase64))
        {
            return false;
        }

        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(providedBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    /// <summary>
    /// Constant-time equality for shared tokens. Both sides are hashed first so the comparison always covers 32 bytes
    /// and the token length does not leak either.
    /// </summary>
    public static bool TokensEqual(string? expected, string? provided)
    {
        if (string.IsNullOrEmpty(expected) || provided is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)));
    }

    private static bool TryFromHex(string? hex, out byte[] bytes)
    {
        bytes = [];
        var trimmed = hex?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(trimmed);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
