using System.Net;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

/// <summary>Hospital-wide settings in memory, as JSON (the store's own deserialisation).</summary>
internal sealed class FakeAbdmSettings : ISettingsService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, string> _rows = [];

    public void Set(string key, string json) => _rows[key] = json;

    public Task<T?> GetAsync<T>(string key, Guid? facilityId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(_rows.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json, Web) : default);

    public Task SetAsync<T>(string key, T value, Guid? facilityId = null, bool secret = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task SetAsync<T>(string key, T value, Guid? facilityId, bool secret, uint? expectedVersion, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<bool> RemoveAsync(string key, Guid? facilityId, CancellationToken cancellationToken = default) => Task.FromResult(_rows.Remove(key));
}

/// <summary>A request the client sent: its path, headers and body.</summary>
internal sealed record SentRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public JsonElement Json()
    {
        using var document = JsonDocument.Parse(Body!);
        return document.RootElement.Clone();
    }

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>Answers the client's calls with queued responses and records every request (no network is used).</summary>
internal sealed class FakeGatewayHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<SentRequest> Requests { get; } = [];

    public FakeGatewayHandler Respond(HttpStatusCode status, string body = "")
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return this;
    }

    public FakeGatewayHandler Throw(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    public HttpClient Client() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new SentRequest(request.Method, request.RequestUri!, headers, body));
        return _responses.Count == 0
            ? throw new InvalidOperationException("No response queued for " + request.RequestUri)
            : _responses.Dequeue()();
    }
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
