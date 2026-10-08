using System.Net;
using System.Text;
using System.Text.Json;

namespace HyFib.HealthCare360.Integrations.Tests.TestSupport;

/// <summary>
/// A request the adapter sent, captured for assertions. <paramref name="ContentTypeHeader"/> is the whole Content-Type
/// header, with its parameters; <paramref name="BodyBytes"/> the body as sent, for binary bodies.
/// </summary>
internal sealed record RecordedRequest(
    HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body, string? ContentType, byte[]? BodyBytes = null,
    string? ContentTypeHeader = null)
{
    public JsonElement Json()
    {
        using var document = JsonDocument.Parse(Body!);
        return document.RootElement.Clone();
    }

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : string.Empty;
}

/// <summary>Answers adapter HTTP calls with queued responses and records every request (no network is used).</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string body, string contentType = "application/json")
    {
        _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });
        return this;
    }

    /// <summary>Answers with whatever <paramref name="respond"/> returns or throws when the request arrives.</summary>
    public FakeHttpHandler Respond(Func<HttpResponseMessage> respond)
    {
        _responses.Enqueue(respond);
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue(() => throw exception);
        return this;
    }

    public HttpClient Client() => new(this, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        Requests.Add(new RecordedRequest(
            request.Method, request.RequestUri!, headers, body, request.Content?.Headers.ContentType?.MediaType, bytes, request.Content?.Headers.ContentType?.ToString()));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No response queued for " + request.RequestUri);
        }

        return _responses.Dequeue()();
    }
}
