using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// Checks that a call to HC360's ABDM callback endpoints comes from the gateway: its <c>Authorization: Bearer</c> token
/// must be signed RS256 by a key in the gateway's published key set (JWKS), and be within its lifetime. Without this
/// anyone who can reach the callback URLs could post a consent notification and have records released; NHA's own
/// reference HIP wrapper does not check, so HC360 does it here. A token whose key id is not in the set is refused as
/// not signed by a published key; the caller then fetches the set again (the gateway may have rotated its keys).
/// </summary>
internal static class AbdmCallbackVerifier
{
    /// <summary>Clock difference tolerated between the gateway and the hospital.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    public const string UnauthenticatedCode = "abdm.callback_unauthenticated";

    /// <summary>The token names a key that is not in the set: the set is to be fetched again before it is refused.</summary>
    public const string UnknownKeyCode = "abdm.callback_unknown_key";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    /// <summary>The token's claims when the call is the gateway's; otherwise why not.</summary>
    /// <param name="jwksJson">The gateway's key set, as it publishes it.</param>
    /// <param name="issuer">The issuer to require, once confirmed against the sandbox; null skips the check.</param>
    /// <param name="audience">The audience to require, likewise (ABDM does not publish it).</param>
    /// <param name="clientId">
    /// The <c>clientId</c> claim to require: the gateway signs every ABDM client's session token with the keys it signs its
    /// own calls with, so without it another provider's token would pass as the gateway's. Null skips the check.
    /// </param>
    public static async Task<Result<IReadOnlyDictionary<string, object>>> VerifyAsync(
        string? authorization, string jwksJson, string? issuer, string? audience, DateTimeOffset now, string? clientId = null)
    {
        const string bearer = "Bearer ";
        if (authorization is null || !authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Rule("abdm.callback_unauthenticated", "The call carries no gateway token.");
        }

        IList<SecurityKey> keys;
        try
        {
            keys = new JsonWebKeySet(jwksJson).GetSigningKeys();
        }
        catch (ArgumentException)
        {
            return Error.Rule("abdm.callback_keys", "The gateway's key set is not valid JWKS.");
        }

        var result = await Handler.ValidateTokenAsync(authorization[bearer.Length..].Trim(), new TokenValidationParameters
        {
            IssuerSigningKeys = keys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuerSigningKey = true,

            // The token's key id must be in the set: an unknown one means the gateway has rotated its keys and the set is to be
            // fetched again, not that any key that happens to verify will do.
            TryAllIssuerSigningKeys = false,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            // The skew is applied to now, never to the token's dates, which may be DateTime.MinValue or MaxValue.
            LifetimeValidator = (notBefore, expires, _, _) =>
                (notBefore is null || notBefore.Value <= now.UtcDateTime + ClockSkew) && expires is { } until && now.UtcDateTime - ClockSkew <= until,
            ValidateIssuer = issuer is not null,
            ValidIssuer = issuer,
            ValidateAudience = audience is not null,
            ValidAudience = audience,
        });

        if (result.IsValid && clientId is not null
            && !(result.Claims.TryGetValue("clientId", out var claimed) && string.Equals(claimed as string, clientId, StringComparison.Ordinal)))
        {
            return Error.Rule(UnauthenticatedCode, "The gateway token was refused: it was issued to another client, not the gateway.");
        }

        return result.IsValid
            ? Result<IReadOnlyDictionary<string, object>>.Ok(result.Claims.AsReadOnly())
            : Error.Rule(
                result.Exception is SecurityTokenSignatureKeyNotFoundException ? UnknownKeyCode : UnauthenticatedCode,
                $"The gateway token was refused: {Reason(result.Exception)}.");
    }

    private static string Reason(Exception? exception) => exception switch
    {
        SecurityTokenInvalidLifetimeException or SecurityTokenExpiredException => "it has expired",
        SecurityTokenSignatureKeyNotFoundException => "it is not signed by a key the gateway publishes",
        SecurityTokenInvalidSignatureException => "its signature does not match",
        SecurityTokenInvalidAlgorithmException => "it is not signed RS256",
        SecurityTokenInvalidIssuerException => "it was not issued by the gateway",
        _ => "it is not a valid signed token",
    };
}
