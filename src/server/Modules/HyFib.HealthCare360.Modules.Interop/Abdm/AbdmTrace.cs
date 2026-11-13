using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>
/// How the ABDM gateway's traffic appears in the integration log (Administration › Integrations › Log, integration
/// <c>abdm</c>): a row for each call HC360 makes to the gateway (<see cref="HttpAbdmGatewayClient"/>, the key-set fetch
/// of <see cref="AbdmGatewayKeys"/>) and for each callback the gateway makes to HC360
/// (<see cref="Features.AbdmGatewayCallbackEndpoints"/>). An outbound row's reference is the call's <c>REQUEST-ID</c>, and a
/// callback's is the <c>REQUEST-ID</c> of the call it answers (<c>response.requestId</c>), so the two rows of one exchange
/// share a reference, and a call whose answer never came is an outbound row with no callback beside it. A row says what
/// was asked and what came back (the HTTP status, the gateway's error code and message), never a body: bodies carry the
/// patient's ABHA address and number, their name, link tokens and the records' names. The platform logger masks ABHA
/// addresses and numbers and tokens in the error text again before the row is stored.
/// </summary>
internal static class AbdmTrace
{
    public const string Integration = "abdm";

    /// <summary>NHA's production gateway or its sandbox, by the consent manager id (<c>abdm</c> or <c>sbx</c>).</summary>
    public static string Provider(string? cmId) => cmId == "abdm" ? "abdm-gateway" : "abdm-sandbox";

    /// <summary>The HTTP status, and the gateway's error code when it gave one: <c>HTTP 400 ABDM-1006</c>.</summary>
    public static string Status(int status, string? errorCode = null) =>
        string.IsNullOrWhiteSpace(errorCode) ? $"HTTP {status}" : $"HTTP {status} {errorCode.Trim()}";

    /// <summary>The request id a callback answers (<c>response.requestId</c>), or null for a call of the gateway's own.</summary>
    public static string? AnsweredRequestId(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
        && response.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    /// <summary>
    /// The error a callback carries (<c>error: { code, message }</c>, the code a string or a number), as
    /// <c>ABDM-1006 Link token expired</c>; null when it carries none.
    /// </summary>
    public static string? CarriedError(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var code = error.TryGetProperty("code", out var c) ? c.ValueKind switch
        {
            JsonValueKind.String => c.GetString(),
            JsonValueKind.Number => c.GetRawText(),
            _ => null,
        } : null;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var text = string.Join(" ", new[] { code, message }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? "The gateway answered with an error." : text;
    }

    /// <summary>The path a URL names, for a call made to a full URL (the key set): what the row's operation says.</summary>
    public static string Operation(Uri url) => url.AbsolutePath.Length <= 64 ? url.AbsolutePath : url.AbsolutePath[^64..];
}
