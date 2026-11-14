using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// DED-08: a write sent again with the same <c>Idempotency-Key</c> never runs twice, whatever became of the first attempt.
/// Its outcome is recorded before the answer is sent, and it runs to its end even when the client goes, so a client that
/// lost the answer (a WiFi roam) gets it on its retry. A first attempt that failed runs again; one that never finished
/// (the server stopped while it ran) is reported as possibly saved, never run again; one still running is waited for.
/// </summary>
public sealed class IdempotencyMiddlewareTests : IDisposable
{
    private const string Key = "attempt-1";
    private const string Receipt = """{"billId":"b1","amount":500,"mode":"Cash"}""";

    /// <summary>A path whose encoded line break (%0D%0A) is decoded into it, to write a line of its own into the log.</summary>
    private const string ForgedPath = "/api/v1/billing/receipts\r\n2026-11-14 09:00:00 [INF] The administrator signed in";

    private static readonly Endpoint Receipts = new(null, EndpointMetadataCollection.Empty, "HTTP: POST /api/v1/billing/receipts");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider();
    private readonly MemoryStore _store = new();
    private readonly Clock _clock = new(new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly Guid _cashier = Guid.NewGuid();
    private readonly FieldProtector _protector;
    private int _runs;

    public IdempotencyMiddlewareTests()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        directories.EnsureCreated();
        _protector = new FieldProtector(directories, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task A_client_that_left_after_the_save_gets_the_first_answer_on_its_retry_and_nothing_runs_twice()
    {
        // The receipt is saved, and the WiFi drops as its answer is sent.
        var first = await SendAsync(SaveReceiptAsync, client: new CancellationTokenSource(), clientLeavesWhenAnswered: true);
        var retry = await SendAsync(SaveReceiptAsync);

        _runs.ShouldBe(1, "The retry must not save a second receipt.");
        first.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.Response.Headers["Idempotent-Replayed"].ToString().ShouldBe("true");
        retry.Response.Headers.Location.ToString().ShouldBe("/api/v1/billing/receipts/r1");
        BodyOf(retry).ShouldBe("""{"receiptNo":"NHR-1"}""");

        var record = _store.Single();
        record.State.ShouldBe(IdempotencyState.Completed);
        record.StatusCode.ShouldBe(StatusCodes.Status201Created);
        record.ResponseBody.ShouldNotBeNull();
        record.ResponseBody.ShouldNotBe(Encoding.UTF8.GetBytes("""{"receiptNo":"NHR-1"}"""), "The stored answer is encrypted.");
    }

    [Fact]
    public async Task A_client_that_leaves_while_the_save_runs_does_not_stop_it_and_its_retry_gets_the_answer()
    {
        var client = new CancellationTokenSource();
        await SendAsync(
            async context =>
            {
                // The WiFi drops while the receipt is being saved; the database calls are given the request's token.
                await client.CancelAsync();
                context.RequestAborted.ThrowIfCancellationRequested();
                await SaveReceiptAsync(context);
            },
            client);
        var retry = await SendAsync(SaveReceiptAsync);

        _runs.ShouldBe(1);
        retry.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.Response.Headers["Idempotent-Replayed"].ToString().ShouldBe("true");
        BodyOf(retry).ShouldBe("""{"receiptNo":"NHR-1"}""", "The whole answer was kept, though the client had gone.");
    }

    [Fact]
    public async Task A_first_attempt_that_failed_runs_again_on_its_retry()
    {
        var failures = 1;
        async Task FailOnceAsync(HttpContext context)
        {
            if (failures-- > 0)
            {
                throw new InvalidOperationException("The database connection was lost.");
            }

            await SaveReceiptAsync(context);
        }

        await Should.ThrowAsync<InvalidOperationException>(() => SendAsync(FailOnceAsync));
        var retry = await SendAsync(FailOnceAsync);

        retry.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.Response.Headers.ContainsKey("Idempotent-Replayed").ShouldBeFalse("The retry ran the request.");
        _runs.ShouldBe(1);
        _store.Single().State.ShouldBe(IdempotencyState.Completed);
    }

    [Fact]
    public async Task A_server_error_is_not_kept_so_the_retry_runs_again()
    {
        var errors = 1;
        Task AnswerAsync(HttpContext context)
        {
            if (errors-- > 0)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return Task.CompletedTask;
            }

            return SaveReceiptAsync(context);
        }

        var first = await SendAsync(AnswerAsync);
        var retry = await SendAsync(AnswerAsync);

        first.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        retry.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        _runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_first_attempt_that_never_finished_is_reported_as_possibly_saved_and_never_run_again()
    {
        // The server stopped while the first attempt ran: it never ends, and its marker stays in progress.
        _ = SendAsync(async context =>
        {
            Interlocked.Increment(ref _runs);
            await new TaskCompletionSource().Task;
        });
        _clock.Now += TimeSpan.FromMinutes(3);

        var retry = await SendAsync(SaveReceiptAsync);
        var later = await SendAsync(SaveReceiptAsync);

        _runs.ShouldBe(1, "Nobody knows whether the first attempt saved the receipt, so it must not be saved again.");
        foreach (var answer in new[] { retry, later })
        {
            answer.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
            var problem = ProblemOf(answer);
            problem.GetProperty("code").GetString().ShouldBe(IdempotencyMiddleware.OutcomeUnknownCode);
            problem.GetProperty("detail").GetString().ShouldBe(
                "An earlier attempt of this request did not finish, so it may or may not have been saved: check before trying again.");
        }

        _store.Single().State.ShouldBe(IdempotencyState.Abandoned);
    }

    [Fact]
    public async Task An_attempt_that_was_only_slow_keeps_its_answer_when_it_ends_after_all()
    {
        var slow = new TaskCompletionSource();
        var first = SendAsync(async context =>
        {
            await slow.Task;
            await SaveReceiptAsync(context);
        });
        _clock.Now += TimeSpan.FromMinutes(3);
        var meanwhile = await SendAsync(SaveReceiptAsync);

        slow.SetResult();
        await first;
        var later = await SendAsync(SaveReceiptAsync);

        meanwhile.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        later.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        later.Response.Headers["Idempotent-Replayed"].ToString().ShouldBe("true");
        _runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_while_the_first_attempt_still_runs_is_told_to_wait_and_then_gets_its_answer()
    {
        var slow = new TaskCompletionSource();
        var first = SendAsync(async context =>
        {
            await slow.Task;
            await SaveReceiptAsync(context);
        });

        var retry = await SendAsync(SaveReceiptAsync);
        slow.SetResult();
        await first;
        var afterwards = await SendAsync(SaveReceiptAsync);

        retry.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        retry.Response.Headers.RetryAfter.ToString().ShouldBe("2");
        ProblemOf(retry).GetProperty("code").GetString().ShouldBe(IdempotencyMiddleware.InProgressCode);
        afterwards.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        afterwards.Response.Headers["Idempotent-Replayed"].ToString().ShouldBe("true");
        _runs.ShouldBe(1);
    }

    [Fact]
    public async Task Two_attempts_that_start_together_run_once()
    {
        // Another attempt with the key is added between this one's look and its own marker.
        _store.BeforeAdd = record => _store.Insert(new IdempotencyRecord
        {
            Scope = record.Scope,
            Key = record.Key,
            RequestHash = record.RequestHash,
            Method = record.Method,
            Path = record.Path,
            State = IdempotencyState.InProgress,
            CreatedAt = record.CreatedAt,
            ExpiresAt = record.ExpiresAt,
        });

        var second = await SendAsync(SaveReceiptAsync);

        second.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        ProblemOf(second).GetProperty("code").GetString().ShouldBe(IdempotencyMiddleware.InProgressCode);
        _runs.ShouldBe(0);
    }

    [Fact]
    public async Task The_same_key_with_another_request_is_refused()
    {
        await SendAsync(SaveReceiptAsync);
        var other = await SendAsync(SaveReceiptAsync, body: """{"billId":"b1","amount":800,"mode":"Cash"}""");

        other.Response.StatusCode.ShouldBe(StatusCodes.Status422UnprocessableEntity);
        _runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_client_that_leaves_names_its_endpoint_in_the_log_never_the_path_it_sent()
    {
        var log = new LogLines();

        await SendAsync(SaveReceiptAsync, clientLeavesWhenAnswered: true, endpoint: Receipts, path: ForgedPath, logger: log);

        log.Lines.ShouldHaveSingleItem().ShouldBe(
            "The client left before the answer to HTTP: POST /api/v1/billing/receipts reached it; a retry gets the recorded outcome");
    }

    [Fact]
    public async Task An_attempt_that_never_finished_names_its_endpoint_in_the_log_never_the_path_it_sent()
    {
        var log = new LogLines();
        _ = SendAsync(_ => new TaskCompletionSource().Task, endpoint: Receipts, path: ForgedPath);
        _clock.Now += TimeSpan.FromMinutes(3);

        await SendAsync(SaveReceiptAsync, endpoint: Receipts, path: ForgedPath, logger: log);

        log.Lines.ShouldHaveSingleItem().ShouldStartWith(
            "The attempt of HTTP: POST /api/v1/billing/receipts with idempotency key attempt-1, begun at ");
        log.Lines.ShouldAllBe(line => !line.Contains('\r') && !line.Contains('\n'));
    }

    [Fact]
    public async Task An_answer_that_is_not_kept_is_not_sent_again_and_not_run_again()
    {
        var endpoint = new Endpoint(null, new EndpointMetadataCollection(NoStoredResponseMetadata.Instance), "dictation");

        var first = await SendAsync(SaveReceiptAsync, endpoint: endpoint);
        var retry = await SendAsync(SaveReceiptAsync, endpoint: endpoint);

        first.Response.StatusCode.ShouldBe(StatusCodes.Status201Created);
        retry.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        ProblemOf(retry).GetProperty("code").GetString().ShouldBe("idempotency.answer_not_kept");
        _store.Single().ResponseBody.ShouldBeNull();
        _runs.ShouldBe(1);
    }

    /// <summary>The endpoint: saves a receipt, with a new number each time it runs, and answers 201 with it.</summary>
    private async Task SaveReceiptAsync(HttpContext context)
    {
        var number = Interlocked.Increment(ref _runs);
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location = $"/api/v1/billing/receipts/r{number}";
        await context.Response.WriteAsJsonAsync(new { receiptNo = $"NHR-{number}" });
    }

    /// <summary>
    /// Sends the receipt with the key, as the cashier's screen does, through the middleware to <paramref name="endpoint"/>.
    /// <paramref name="client"/> is the client's connection: cancelled when it goes. With
    /// <paramref name="clientLeavesWhenAnswered"/> it goes as its answer is sent.
    /// </summary>
    private async Task<HttpContext> SendAsync(
        RequestDelegate handler,
        CancellationTokenSource? client = null,
        bool clientLeavesWhenAnswered = false,
        string body = Receipt,
        Endpoint? endpoint = null,
        string path = "/api/v1/billing/receipts",
        ILogger<IdempotencyMiddleware>? logger = null)
    {
        client ??= new CancellationTokenSource();
        var http = new DefaultHttpContext
        {
            RequestServices = _services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HcClaims.UserId, _cashier.ToString())], "test")),
        };
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = path;
        http.Request.ContentType = "application/json";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.Headers[IdempotencyMiddleware.HeaderName] = Key;
        http.RequestAborted = client.Token;
        http.Response.Body = clientLeavesWhenAnswered ? new ClientThatLeaves(client) : new MemoryStream();
        if (endpoint is not null)
        {
            http.SetEndpoint(endpoint);
        }

        var middleware = new IdempotencyMiddleware(handler, logger ?? NullLogger<IdempotencyMiddleware>.Instance);
        await middleware.HandleAsync(http, Key, _store, new HttpCurrentUser(new HttpContextAccessor { HttpContext = http }), _protector, _clock);
        return http;
    }

    private static string BodyOf(HttpContext http)
    {
        var body = (MemoryStream)http.Response.Body;
        return Encoding.UTF8.GetString(body.ToArray());
    }

    private static JsonElement ProblemOf(HttpContext http)
    {
        using var document = JsonDocument.Parse(BodyOf(http));
        return document.RootElement.Clone();
    }

    /// <summary>The lines the middleware writes to its log, as a log file holds them.</summary>
    private sealed class LogLines : ILogger<IdempotencyMiddleware>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// The connection to a client that goes as its answer is sent: writing fails as Kestrel's write does once the request
    /// is aborted.
    /// </summary>
    private sealed class ClientThatLeaves(CancellationTokenSource client) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Leave();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(Left());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(Left());

        private void Leave() => throw Left();

        private OperationCanceledException Left()
        {
            client.Cancel();
            return new OperationCanceledException("The client disconnected.", client.Token);
        }
    }

    /// <summary>The records in memory. Each method is one atomic step, as each of the database's statements is.</summary>
    private sealed class MemoryStore : IIdempotencyStore
    {
        private readonly Lock _gate = new();
        private readonly List<IdempotencyRecord> _records = [];

        /// <summary>Runs once, just before the next marker is added: another attempt with the same key gets in first.</summary>
        public Action<IdempotencyRecord>? BeforeAdd { get; set; }

        public IdempotencyRecord Single()
        {
            lock (_gate)
            {
                return Copy(_records.Single());
            }
        }

        public void Insert(IdempotencyRecord record)
        {
            lock (_gate)
            {
                _records.Add(Copy(record));
            }
        }

        public Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(_records.Where(r => r.Scope == scope && r.Key == key).Select(Copy).FirstOrDefault());
            }
        }

        public Task<bool> TryAddAsync(IdempotencyRecord record, CancellationToken cancellationToken)
        {
            var before = BeforeAdd;
            BeforeAdd = null;
            before?.Invoke(record);
            lock (_gate)
            {
                if (_records.Any(r => r.Scope == record.Scope && r.Key == record.Key))
                {
                    return Task.FromResult(false);
                }

                _records.Add(Copy(record));
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryAbandonAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var record = _records.SingleOrDefault(r => r.Id == id && r.State == IdempotencyState.InProgress);
                if (record is null)
                {
                    return Task.FromResult(false);
                }

                record.State = IdempotencyState.Abandoned;
                return Task.FromResult(true);
            }
        }

        public Task SaveOutcomeAsync(IdempotencyRecord record, CancellationToken cancellationToken)
        {
            Update(record.Id, r =>
            {
                r.State = record.State;
                r.StatusCode = record.StatusCode;
                r.ContentType = record.ContentType;
                r.Location = record.Location;
                r.ResponseBody = record.ResponseBody;
            });
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _records.RemoveAll(r => r.Id == id);
            }

            return Task.CompletedTask;
        }

        private void Update(Guid id, Action<IdempotencyRecord> change)
        {
            lock (_gate)
            {
                if (_records.SingleOrDefault(r => r.Id == id) is { } record)
                {
                    change(record);
                }
            }
        }

        private static IdempotencyRecord Copy(IdempotencyRecord r) => new()
        {
            Id = r.Id,
            Scope = r.Scope,
            Key = r.Key,
            RequestHash = r.RequestHash,
            Method = r.Method,
            Path = r.Path,
            State = r.State,
            StatusCode = r.StatusCode,
            ContentType = r.ContentType,
            Location = r.Location,
            ResponseBody = r.ResponseBody?.ToArray(),
            CreatedAt = r.CreatedAt,
            ExpiresAt = r.ExpiresAt,
        };
    }
}
