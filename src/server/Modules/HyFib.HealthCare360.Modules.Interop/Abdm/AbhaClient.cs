using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Interop.Abdm;

/// <summary>How a patient's ABHA is looked up for verification: by its number, or the mobile number linked to it.</summary>
public enum AbhaLoginHint
{
    AbhaNumber,
    Mobile,
}

/// <summary>An ABHA account as ABDM returns it after the patient's code is verified.</summary>
internal sealed record AbhaAccount(
    [property: JsonPropertyName("ABHANumber")] string? AbhaNumber,
    string? PreferredAbhaAddress,
    string? Name,
    string? Gender,
    string? Dob,
    string? Status,
    [property: JsonConverter(typeof(LenientBoolConverter))] bool? KycVerified);

/// <summary>Reads <c>true</c>/<c>false</c> as booleans or strings; anything else as null.</summary>
internal sealed class LenientBoolConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.String:
                return bool.TryParse(reader.GetString(), out var value) ? value : null;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } flag)
        {
            writer.WriteBooleanValue(flag);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>ABDM's answer to a login code: whether it was right, why not, and the accounts it opens.</summary>
internal sealed record AbhaLoginResult(string? TxnId, string? AuthResult, string? Message, IReadOnlyList<AbhaAccount>? Accounts)
{
    public bool Succeeded => string.Equals(AuthResult, "success", StringComparison.OrdinalIgnoreCase);
}

/// <summary>ABDM's answer to a request for a login code: the transaction, and where the code went.</summary>
internal sealed record AbhaOtpSent(string TxnId, string? Message);

/// <summary>
/// ABDM's ABHA service (v3, <see cref="AbdmGatewaySettings.AbhaServiceUrl"/>), for checking a patient's ABHA at the desk:
/// <list type="number">
/// <item><c>POST /profile/login/request/otp</c>: <c>{ scope: ["abha-login", "mobile-verify"], loginHint: "abha-number"
/// | "mobile", loginId, otpSystem: "abdm" }</c>; ABDM sends a code to the mobile number linked to the ABHA and answers
/// <c>{ txnId, message }</c>.</item>
/// <item><c>POST /profile/login/verify</c>: <c>{ scope, authData: { authMethods: ["otp"], otp: { txnId, otpValue } } }</c>;
/// ABDM answers <c>{ txnId, authResult, message, token, accounts: [{ ABHANumber, preferredAbhaAddress, name, gender, dob,
/// status, kycVerified }] }</c>. The token, which opens the patient's ABHA profile, is not kept.</item>
/// </list>
/// The login id and the code are encrypted with ABDM's public key (<c>GET /profile/public/certificate</c>, kept half an
/// hour) under RSA-OAEP with SHA-1 (<c>RSA/ECB/OAEPWithSHA-1AndMGF1Padding</c>), base64. Each call carries the gateway
/// session (<c>Authorization: Bearer</c>), <c>X-CM-ID</c>, <c>REQUEST-ID</c> and <c>TIMESTAMP</c>, as the gateway's
/// calls do. The shapes follow ABDM's V3 integrator guide as integrators use them against the sandbox; they are to be
/// confirmed in the hospital's own sandbox run.
/// </summary>
internal sealed class AbhaClient(
    HttpClient http,
    AbdmGatewayConfiguration configuration,
    IAbdmGatewayClient gateway,
    TimeProvider time,
    ILogger<AbhaClient> log)
{
    public const string CertificatePath = "/profile/public/certificate";
    public const string RequestOtpPath = "/profile/login/request/otp";
    public const string VerifyPath = "/profile/login/verify";

    public const string RefusedCode = "abdm.abha_refused";
    public const string UnreachableCode = "abdm.abha_unreachable";

    /// <summary>How long ABDM's public key is kept before it is fetched again.</summary>
    public static readonly TimeSpan KeyLifetime = TimeSpan.FromMinutes(30);

    private static readonly string[] Scope = ["abha-login", "mobile-verify"];
    private static readonly object KeyGate = new();
    private static (string Url, byte[] PublicKey, DateTimeOffset FetchedAt)? _key;

    public async Task<Result<AbhaOtpSent>> RequestOtpAsync(AbhaLoginHint hint, string loginId, CancellationToken ct)
    {
        var encrypted = await EncryptAsync(loginId, ct);
        if (encrypted.IsFailure)
        {
            return encrypted.Error!;
        }

        var body = new
        {
            scope = Scope,
            loginHint = hint == AbhaLoginHint.AbhaNumber ? "abha-number" : "mobile",
            loginId = encrypted.Value,
            otpSystem = "abdm",
        };
        var answer = await PostAsync<OtpAnswer>(RequestOtpPath, body, "send the patient a code", ct);
        if (answer.IsFailure)
        {
            return answer.Error!;
        }

        return string.IsNullOrWhiteSpace(answer.Value.TxnId)
            ? Error.Rule(RefusedCode, $"ABDM sent no code: {answer.Value.Message ?? "its answer had no transaction"}.")
            : new AbhaOtpSent(answer.Value.TxnId, answer.Value.Message);
    }

    public async Task<Result<AbhaLoginResult>> VerifyAsync(string txnId, string otp, CancellationToken ct)
    {
        var encrypted = await EncryptAsync(otp, ct);
        if (encrypted.IsFailure)
        {
            return encrypted.Error!;
        }

        var body = new { scope = Scope, authData = new { authMethods = new[] { "otp" }, otp = new { txnId, otpValue = encrypted.Value } } };
        return await PostAsync<AbhaLoginResult>(VerifyPath, body, "check the patient's code", ct);
    }

    /// <summary>The value encrypted with ABDM's public key, as ABDM takes identifiers and codes: RSA-OAEP, SHA-1, base64.</summary>
    public static string Encrypt(byte[] subjectPublicKeyInfo, string value)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
        return Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(value), RSAEncryptionPadding.OaepSHA1));
    }

    /// <summary>
    /// ABDM's public key from its certificate endpoint: <c>{ publicKey }</c>, <c>{ certificate }</c> or the text itself, as
    /// a base64 or PEM public key (SubjectPublicKeyInfo) or X.509 certificate. Null when it is none of those.
    /// </summary>
    public static byte[]? ReadPublicKey(string answer)
    {
        var text = answer.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                text = (document.RootElement.TryGetProperty("publicKey", out var key) ? key.GetString()
                    : document.RootElement.TryGetProperty("certificate", out var certificate) ? certificate.GetString()
                    : null) ?? string.Empty;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        var base64 = string.Concat(text.Split('\n').Select(l => l.Trim()).Where(l => !l.StartsWith("-----", StringComparison.Ordinal)));
        byte[] der;
        try
        {
            der = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(der, out _);
            return rsa.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            using var rsa = certificate.GetRSAPublicKey();
            return rsa?.ExportSubjectPublicKeyInfo();
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task<Result<string>> EncryptAsync(string value, CancellationToken ct)
    {
        var key = await PublicKeyAsync(ct);
        return key.IsFailure ? key.Error! : Encrypt(key.Value, value);
    }

    private async Task<Result<byte[]>> PublicKeyAsync(CancellationToken ct)
    {
        var (settings, _, _) = await configuration.ReadAsync(ct);
        var url = settings.AbhaServiceUrl + CertificatePath;
        var now = time.GetUtcNow();
        lock (KeyGate)
        {
            if (_key is { } kept && kept.Url == url && now - kept.FetchedAt < KeyLifetime)
            {
                return kept.PublicKey;
            }
        }

        var session = await gateway.CreateSessionAsync(ct);
        if (session.IsFailure)
        {
            return session.Error!;
        }

        using var request = Request(HttpMethod.Get, url, settings, session.Value, content: null);
        var answer = await ExchangeAsync(request, settings, "fetch ABDM's public key", ct);
        if (answer.IsFailure)
        {
            return answer.Error!;
        }

        using var response = answer.Value;
        var text = await response.Content.ReadAsStringAsync(ct);
        var key = response.IsSuccessStatusCode && text.Length < 16_384 ? ReadPublicKey(text) : null;
        if (key is null)
        {
            log.LogWarning("ABDM's public key could not be read: HTTP {Status}", (int)response.StatusCode);
            return Error.Rule(RefusedCode, $"ABDM's public key could not be read (HTTP {(int)response.StatusCode}).");
        }

        lock (KeyGate)
        {
            _key = (url, key, now);
        }

        return key;
    }

    private async Task<Result<T>> PostAsync<T>(string path, object body, string what, CancellationToken ct)
        where T : class
    {
        var (settings, _, _) = await configuration.ReadAsync(ct);
        var session = await gateway.CreateSessionAsync(ct);
        if (session.IsFailure)
        {
            return session.Error!;
        }

        using var request = Request(
            HttpMethod.Post,
            settings.AbhaServiceUrl + path,
            settings,
            session.Value,
            new StringContent(JsonSerializer.Serialize(body, AbdmGatewayMessages.Json), Encoding.UTF8, "application/json"));
        var answer = await ExchangeAsync(request, settings, what, ct);
        if (answer.IsFailure)
        {
            return answer.Error!;
        }

        using var response = answer.Value;
        var text = await response.Content.ReadAsStringAsync(ct);
        T? parsed = null;
        try
        {
            parsed = text.Length is > 0 and < 65_536 ? JsonSerializer.Deserialize<T>(text, AbdmGatewayMessages.Json) : null;
        }
        catch (JsonException)
        {
        }

        if (response.IsSuccessStatusCode && parsed is not null)
        {
            return parsed;
        }

        // A wrong code comes back as a 4xx with the verify answer's own shape on some versions: let the caller read it.
        if (parsed is AbhaLoginResult { AuthResult: not null })
        {
            return parsed;
        }

        var error = AbdmGatewayMessages.ReadError(text.Length > 4096 ? text[..4096] : text);
        var detail = error is null ? $"HTTP {(int)response.StatusCode}"
            : string.Join(" ", new[] { error.Code, error.Message }.Where(s => !string.IsNullOrWhiteSpace(s)));
        log.LogWarning("ABDM refused to {What}: {Status} {Detail}", what, (int)response.StatusCode, detail);
        return Error.Rule(RefusedCode, $"ABDM refused to {what}: {detail}.");
    }

    private HttpRequestMessage Request(HttpMethod method, string url, AbdmGatewaySettings settings, string session, HttpContent? content)
    {
        var request = new HttpRequestMessage(method, new Uri(url)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session);
        request.Headers.Add("X-CM-ID", settings.CmId);
        request.Headers.Add("REQUEST-ID", Guid.CreateVersion7().ToString("D"));
        request.Headers.Add("TIMESTAMP", HttpAbdmGatewayClient.Timestamp(time.GetUtcNow()));
        return request;
    }

    private async Task<Result<HttpResponseMessage>> ExchangeAsync(HttpRequestMessage request, AbdmGatewaySettings settings, string what, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 5, 120)));
        try
        {
            return await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "ABDM's ABHA service could not be reached to {What}", what);
            return Error.Rule(UnreachableCode, $"ABDM could not be reached to {what}. Try again in a moment.");
        }
    }

    private sealed record OtpAnswer(string? TxnId, string? Message);
}
