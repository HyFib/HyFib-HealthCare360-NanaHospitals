using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>
/// Cashfree's webhook signature, as its official SDKs (cashfree-pg Node 6.0.6 <c>PGVerifyWebhookSignature</c>, Python
/// 6.0.1) compute it: the standard Base64 of HMAC-SHA256, keyed with the client secret, over the
/// <c>x-webhook-timestamp</c> header followed directly by the raw body. The SDKs compare with plain string equality and
/// never look at the timestamp; HC360 compares in constant time and refuses a delivery signed outside
/// <see cref="Window"/> or more than <see cref="Skew"/> ahead, so a captured delivery cannot be replayed for ever.
/// </summary>
internal static class CashfreeSignatures
{
    /// <summary>How long a signed delivery is accepted after its timestamp (the gateway's retries come within it).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(3);

    /// <summary>How far ahead of HC360's clock a timestamp may be (the two clocks drift a little).</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    public static bool Verify(string? clientSecret, string payload, string? signature, string? timestamp, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(clientSecret) || string.IsNullOrWhiteSpace(signature) || TimeOf(timestamp) is not { } signedAt)
        {
            return false;
        }

        if (signedAt < now - Window || signedAt > now + Skew)
        {
            return false;
        }

        return Signatures.VerifyHmacSha256Base64(clientSecret, timestamp!.Trim() + payload, signature);
    }

    /// <summary>
    /// The signed time: Unix seconds or milliseconds (the SDKs do not say which, so a value past the year 5138 in
    /// seconds is read as milliseconds); anything else is no time at all.
    /// </summary>
    public static DateTimeOffset? TimeOf(string? timestamp)
    {
        var text = timestamp?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 16 || !text.All(char.IsAsciiDigit)
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        return value >= 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
    }

    /// <summary>The signature Cashfree would send for <paramref name="payload"/> at <paramref name="timestamp"/> (tests and the training tools).</summary>
    public static string Sign(string clientSecret, string timestamp, string payload) =>
        Convert.ToBase64String(Signatures.HmacSha256(clientSecret, System.Text.Encoding.UTF8.GetBytes(timestamp + payload)));
}
