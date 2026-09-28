namespace HyFib.HealthCare360.Modules.VirtualCare.Domain;

/// <summary>
/// Patient join links for telehealth (TEL-01). A link carries a 256-bit random token; the database keeps only
/// SHA-256("{sessionId}:{token}"), so a copied database cannot be turned into working links and a token can never
/// open a different session. Comparison is constant-time so response timing reveals nothing about the stored hash.
/// </summary>
public static class JoinTokens
{
    /// <summary>Longer inputs are rejected before hashing (a real token is 43 characters).</summary>
    public const int MaxTokenLength = 128;

    public static string NewToken() => SecretTokens.NewOpaqueToken(32);

    public static string Hash(Guid sessionId, string token) => SecretTokens.Hash($"{sessionId:N}:{token}");

    public static bool Matches(Guid sessionId, string? token, string? storedHash) =>
        !string.IsNullOrEmpty(token) && token.Length <= MaxTokenLength && !string.IsNullOrEmpty(storedHash)
        && SecretTokens.FixedTimeEquals(storedHash, Hash(sessionId, token));
}
