using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The ABDM gateway as the test server sees it (<see cref="HospitalServer.AbdmGateway"/>): it starts sessions, accepts
/// the HIP's calls with 202 as the sandbox does, publishes the key set its callbacks are signed with, and records every
/// call. A test answers a call differently with <see cref="Answer"/>, and calls HC360 back with <see cref="Token"/>.
/// No request leaves the test server.
/// </summary>
public sealed class FakeAbdmGateway
{
    /// <summary>The base URL the gateway set-up of a test names.</summary>
    public const string BaseUrl = "https://abdm-gateway.test/api/hiecm";

    private const string KeyId = "it-gateway";

    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentQueue<GatewayCall> _calls = new();
    private Func<GatewayCall, HttpResponseMessage?>? _answer;

    /// <summary>Every call the server made, oldest first.</summary>
    public IReadOnlyList<GatewayCall> Calls => [.. _calls];

    /// <summary>The calls to <paramref name="path"/> (under <see cref="BaseUrl"/>).</summary>
    public IReadOnlyList<GatewayCall> CallsTo(string path) => [.. _calls.Where(c => c.Path == path)];

    /// <summary>Answers the calls <paramref name="answer"/> returns a response for; the others as the sandbox does.</summary>
    public void Answer(Func<GatewayCall, HttpResponseMessage?>? answer) => _answer = answer;

    /// <summary>A bearer token signed with the gateway's key, valid for ten minutes, as its callbacks carry.</summary>
    public string Token()
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://abdm-gateway.test/gateway",
            Claims = new Dictionary<string, object> { ["clientId"] = "gateway" },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A bearer token signed by a key the gateway does not publish.</summary>
    public static string ForgedToken()
    {
        using var other = RSA.Create(2048);
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(other) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>A handler for the server's HTTP clients that sends their requests here.</summary>
    public HttpMessageHandler Handler() => new Forward(this);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var uri = request.RequestUri!.ToString();
        var call = new GatewayCall(
            request.Method,
            uri.StartsWith(BaseUrl, StringComparison.Ordinal) ? uri[BaseUrl.Length..] : uri,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase),
            body);
        _calls.Enqueue(call);

        return _answer?.Invoke(call) ?? call.Path switch
        {
            "/gateway/v3/certs" => Json(HttpStatusCode.OK, KeySet()),
            "/gateway/v3/sessions" => Json(HttpStatusCode.OK, """{"accessToken":"it-session","expiresIn":1200,"tokenType":"bearer"}"""),
            _ when request.Method == HttpMethod.Post => new HttpResponseMessage(HttpStatusCode.Accepted),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private string KeySet()
    {
        var key = _key.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) },
            },
        });
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Forward(FakeAbdmGateway gateway) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            gateway.SendAsync(request, cancellationToken);
    }
}

/// <summary>A call the server made to the gateway: its path under the base URL, headers and body.</summary>
public sealed record GatewayCall(HttpMethod Method, string Path, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;

    public JsonElement Json()
    {
        using var document = JsonDocument.Parse(Body!);
        return document.RootElement.Clone();
    }
}
