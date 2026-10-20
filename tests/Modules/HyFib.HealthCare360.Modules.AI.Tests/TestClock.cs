using System.Net;
using System.Text;

namespace HyFib.HealthCare360.Modules.AI.Tests;

/// <summary>
/// A clock tests can set. The default instant is Friday 25 September 2026, 10:30 IST (05:00 UTC), the "today" every
/// assistant test reasons about.
/// </summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public static readonly DateTimeOffset Friday = new(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 9, 25);

    public TestClock()
        : this(Friday)
    {
    }

    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Answers HTTP calls with queued responses and records the requests (no network is used).</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    /// <summary>The token each request was sent with.</summary>
    public List<CancellationToken> Tokens { get; } = [];

    public StubHttpHandler Respond(HttpStatusCode status, string body)
    {
        _responses.Enqueue(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        return this;
    }

    public StubHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    /// <summary>Never answers: the request waits until it is cancelled.</summary>
    public StubHttpHandler Hang()
    {
        _responses.Enqueue(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        Tokens.Add(cancellationToken);
        return _responses.Count == 0 ? throw new InvalidOperationException("No response queued.") : await _responses.Dequeue()(cancellationToken);
    }
}
