using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Integrations.Nhcx;

/// <summary>
/// The key-encryption algorithm of an NHCX message. The HCX protocol specification fixes <c>RSA-OAEP</c> (SHA-1), while
/// the HCX reference applications encrypt with <c>RSA-OAEP-256</c>; both are read, and the one sent is a setting.
/// </summary>
public enum NhcxKeyAlgorithm
{
    RsaOaep256 = 0,
    RsaOaep,
}

/// <summary>
/// The HCX protocol headers of a message, carried in clear (but integrity-protected) in the JWE's protected header so
/// the gateway can route and audit it. They never carry anything about the patient.
/// </summary>
/// <param name="SenderCode">The sender's participant code in the NHCX registry.</param>
/// <param name="RecipientCode">The recipient's participant code.</param>
/// <param name="ApiCallId">Unique for every call.</param>
/// <param name="CorrelationId">The same for every call of one claim cycle (request, response, communications).</param>
/// <param name="WorkflowId">Optional: the same for every cycle of one episode (pre-authorisation, then the claim).</param>
/// <param name="Status">For requests <c>request.queued</c> or <c>request.dispatched</c>; for responses
/// <c>response.complete</c>, <c>response.partial</c>, <c>response.error</c> or <c>response.redirect</c>.</param>
public sealed record NhcxHeaders(
    string SenderCode,
    string RecipientCode,
    Guid ApiCallId,
    Guid CorrelationId,
    Guid? WorkflowId,
    DateTimeOffset Timestamp,
    string? Status);

/// <summary>An opened NHCX message: its protocol headers, the whole protected header, and the FHIR payload.</summary>
public sealed record NhcxOpened(NhcxHeaders Headers, JsonObject ProtectedHeader, string Payload);

/// <summary>
/// The NHCX (HCX protocol) message envelope: a JWE in compact serialisation (RFC 7516) whose protected header joins the
/// JOSE header (<c>alg</c>, <c>enc</c>: A256GCM) and the <c>x-hcx-*</c> protocol headers, and whose ciphertext is the FHIR
/// bundle, so the gateway can route the message but only the recipient can read it. The content key is wrapped with the
/// recipient's RSA public key (from their encryption certificate in the registry); the payload is AES-256-GCM with the
/// ASCII of the encoded protected header as additional authenticated data, so the headers cannot be changed in transit.
/// The posted body is <c>{"payload": "&lt;compact JWE&gt;"}</c>.
/// </summary>
public static class NhcxEnvelope
{
    public const string RequestQueued = "request.queued";

    public static readonly IReadOnlyList<string> RequestStatuses = [RequestQueued, "request.dispatched"];

    public static readonly IReadOnlyList<string> ResponseStatuses = ["response.complete", "response.partial", "response.error", "response.redirect"];

    private const string Enc = "A256GCM";

    /// <summary>Encrypts <paramref name="payloadJson"/> for the holder of <paramref name="recipientPublicKey"/>.</summary>
    public static string Seal(string payloadJson, NhcxHeaders headers, RSA recipientPublicKey, NhcxKeyAlgorithm algorithm = NhcxKeyAlgorithm.RsaOaep256)
    {
        var header = new JsonObject
        {
            ["alg"] = Name(algorithm),
            ["enc"] = Enc,
            ["x-hcx-sender_code"] = headers.SenderCode,
            ["x-hcx-recipient_code"] = headers.RecipientCode,
            ["x-hcx-api_call_id"] = headers.ApiCallId.ToString(),
            ["x-hcx-correlation_id"] = headers.CorrelationId.ToString(),
            ["x-hcx-timestamp"] = headers.Timestamp.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
        };
        if (headers.WorkflowId is { } workflow)
        {
            header["x-hcx-workflow_id"] = workflow.ToString();
        }

        if (headers.Status is { } status)
        {
            header["x-hcx-status"] = status;
        }

        var encodedHeader = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString()));
        var key = RandomNumberGenerator.GetBytes(32);
        var iv = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(payloadJson);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Encrypt(iv, plain, cipher, tag, Encoding.ASCII.GetBytes(encodedHeader));
        }

        var wrapped = recipientPublicKey.Encrypt(key, Padding(algorithm));
        CryptographicOperations.ZeroMemory(key);
        return string.Join('.', encodedHeader, Base64Url.EncodeToString(wrapped), Base64Url.EncodeToString(iv), Base64Url.EncodeToString(cipher),
            Base64Url.EncodeToString(tag));
    }

    /// <summary>Decrypts a message with the hospital's private key; refused when it is malformed, altered or not for this key.</summary>
    public static Result<NhcxOpened> Open(string compact, RSA privateKey)
    {
        var parts = compact.Split('.');
        if (parts.Length != 5)
        {
            return Error.Rule("nhcx.malformed", "An NHCX message is a JWE in compact form: five parts separated by dots.");
        }

        JsonObject header;
        byte[] wrapped, iv, cipher, tag;
        try
        {
            header = JsonNode.Parse(Base64Url.DecodeFromChars(parts[0])) as JsonObject
                ?? throw new FormatException("The protected header is not a JSON object.");
            wrapped = Base64Url.DecodeFromChars(parts[1]);
            iv = Base64Url.DecodeFromChars(parts[2]);
            cipher = Base64Url.DecodeFromChars(parts[3]);
            tag = Base64Url.DecodeFromChars(parts[4]);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return Error.Rule("nhcx.malformed", "The NHCX message is not valid base64url JSON.");
        }

        if ((string?)header["enc"] != Enc)
        {
            return Error.Rule("nhcx.algorithm", $"NHCX messages are encrypted with {Enc}; this one says {(string?)header["enc"] ?? "nothing"}.");
        }

        NhcxKeyAlgorithm algorithm;
        switch ((string?)header["alg"])
        {
            case "RSA-OAEP-256":
                algorithm = NhcxKeyAlgorithm.RsaOaep256;
                break;
            case "RSA-OAEP":
                algorithm = NhcxKeyAlgorithm.RsaOaep;
                break;
            default:
                return Error.Rule("nhcx.algorithm", $"NHCX keys are wrapped with RSA-OAEP or RSA-OAEP-256; this one says {(string?)header["alg"] ?? "nothing"}.");
        }

        if (iv.Length != 12 || tag.Length != 16)
        {
            return Error.Rule("nhcx.malformed", "An A256GCM message has a 12-byte IV and a 16-byte tag.");
        }

        byte[] key;
        try
        {
            key = privateKey.Decrypt(wrapped, Padding(algorithm));
        }
        catch (CryptographicException)
        {
            return Error.Rule("nhcx.not_for_us", "The message's key does not open with this hospital's private key: it was sent to someone else, or altered.");
        }

        if (key.Length != 32)
        {
            return Error.Rule("nhcx.malformed", "An A256GCM content key is 32 bytes.");
        }

        var plain = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(iv, cipher, tag, plain, Encoding.ASCII.GetBytes(parts[0]));
        }
        catch (AuthenticationTagMismatchException)
        {
            return Error.Rule("nhcx.altered", "The NHCX message was altered after it was sealed: its headers or payload do not match its tag.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var headers = ReadHeaders(header);
        return headers.IsFailure ? headers.Error! : new NhcxOpened(headers.Value, header, Encoding.UTF8.GetString(plain));
    }

    /// <summary>
    /// What the HCX gateway checks of the protocol headers before it routes a message: the codes present, the ids UUIDs,
    /// the status one allowed for a request or a response, and the timestamp neither in the future nor older than
    /// <paramref name="maxAge"/>. A server clock running ahead has its messages refused, so the hospital's clock must be
    /// kept to time (HC360 checks it against NIC and NPL).
    /// </summary>
    public static Result Validate(NhcxHeaders headers, bool isResponse, DateTimeOffset now, TimeSpan maxAge)
    {
        if (string.IsNullOrWhiteSpace(headers.SenderCode) || string.IsNullOrWhiteSpace(headers.RecipientCode))
        {
            return Error.Rule("nhcx.headers", "The sender and recipient participant codes are required.");
        }

        if (headers.ApiCallId == Guid.Empty || headers.CorrelationId == Guid.Empty)
        {
            return Error.Rule("nhcx.headers", "The API call id and correlation id are required.");
        }

        var allowed = isResponse ? ResponseStatuses : RequestStatuses;
        if ((isResponse || headers.Status is not null) && !allowed.Contains(headers.Status))
        {
            return Error.Rule("nhcx.headers", $"The status of a {(isResponse ? "response" : "request")} is one of {string.Join(", ", allowed)}.");
        }

        if (headers.Timestamp > now)
        {
            return Error.Rule("nhcx.timestamp", "The message's timestamp is in the future: check the server's clock.");
        }

        return headers.Timestamp < now - maxAge
            ? Error.Rule("nhcx.timestamp", $"The message is older than {maxAge.TotalHours:0} hours.")
            : Result.Success();
    }

    private static Result<NhcxHeaders> ReadHeaders(JsonObject header)
    {
        string? Text(string name) => header[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        if (!Guid.TryParse(Text("x-hcx-api_call_id"), out var apiCall) || !Guid.TryParse(Text("x-hcx-correlation_id"), out var correlation))
        {
            return Error.Rule("nhcx.headers", "The message's API call id and correlation id must be UUIDs.");
        }

        Guid? workflow = null;
        if (Text("x-hcx-workflow_id") is { } workflowText)
        {
            if (!Guid.TryParse(workflowText, out var parsed))
            {
                return Error.Rule("nhcx.headers", "The message's workflow id must be a UUID.");
            }

            workflow = parsed;
        }

        if (!DateTimeOffset.TryParse(Text("x-hcx-timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
        {
            return Error.Rule("nhcx.headers", "The message's timestamp is not an ISO-8601 date and time.");
        }

        return new NhcxHeaders(Text("x-hcx-sender_code") ?? string.Empty, Text("x-hcx-recipient_code") ?? string.Empty, apiCall, correlation, workflow,
            timestamp, Text("x-hcx-status"));
    }

    private static string Name(NhcxKeyAlgorithm algorithm) => algorithm == NhcxKeyAlgorithm.RsaOaep ? "RSA-OAEP" : "RSA-OAEP-256";

    private static RSAEncryptionPadding Padding(NhcxKeyAlgorithm algorithm) =>
        algorithm == NhcxKeyAlgorithm.RsaOaep ? RSAEncryptionPadding.OaepSHA1 : RSAEncryptionPadding.OaepSHA256;
}
