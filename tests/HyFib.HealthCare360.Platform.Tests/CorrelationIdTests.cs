using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS OBS-04: one correlation id follows a request end to end, into the exception handler's log entries and onto the
/// server's own calls to outside services.
/// </summary>
public sealed class CorrelationIdTests
{
    [Fact]
    public async Task The_exception_handler_logs_under_the_requests_correlation_id()
    {
        var logs = new CapturingLoggerProvider();
        await using var services = Services(logs);
        var app = new ApplicationBuilder(services);
        app.UseCorrelationId();
        app.UseExceptionHandler();
        app.Run(_ => throw new InvalidOperationException("boom"));
        var context = Request(services, "abc-123");

        await app.Build()(context);
        await StartResponseAsync(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        context.Response.Headers[CorrelationIdMiddleware.HeaderName].ToString().ShouldBe("abc-123");
        logs.Entries.ShouldContain(entry => entry.Category == typeof(GlobalExceptionHandler).FullName && entry.CorrelationId == "abc-123");
    }

    [Fact]
    public async Task The_middleware_is_added_once_however_often_it_is_asked_for()
    {
        await using var services = Services(new CapturingLoggerProvider());
        var app = new ApplicationBuilder(services);
        var seen = new List<string?>();
        app.UseCorrelationId();
        app.Use(async (context, next) =>
        {
            seen.Add(CorrelationContext.Current);
            context.Request.Headers[CorrelationIdMiddleware.HeaderName] = "changed";
            await next(context);
        });
        app.UseCorrelationId();
        app.Run(context =>
        {
            seen.Add(CorrelationContext.Current);
            return Task.CompletedTask;
        });

        await app.Build()(Request(services, "first"));

        seen.ShouldBe(["first", "first"]);
        CorrelationContext.Current.ShouldBeNull();
    }

    [Fact]
    public async Task An_unsafe_incoming_id_is_replaced()
    {
        await using var services = Services(new CapturingLoggerProvider());
        var app = new ApplicationBuilder(services);
        app.UseCorrelationId();
        app.Run(_ => Task.CompletedTask);
        var context = Request(services, "bad id\r\nX-Injected: 1");

        await app.Build()(context);

        context.TraceIdentifier.ShouldNotContain(" ");
        context.TraceIdentifier.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Outbound_calls_carry_the_current_correlation_id()
    {
        var inner = new RecordingHandler();
        using var http = new HttpClient(new CorrelationIdForwardingHandler { InnerHandler = inner });

        using (CorrelationContext.Begin("req-42"))
        {
            using var response = await http.GetAsync(new Uri("https://provider.example/api"), TestContext.Current.CancellationToken);
        }

        inner.Headers.ShouldHaveSingleItem().ShouldBe("req-42");
    }

    [Fact]
    public async Task An_outbound_call_outside_any_request_carries_no_id_and_an_explicit_one_is_kept()
    {
        var inner = new RecordingHandler();
        using var http = new HttpClient(new CorrelationIdForwardingHandler { InnerHandler = inner });
        var ct = TestContext.Current.CancellationToken;

        using (await http.GetAsync(new Uri("https://provider.example/api"), ct))
        {
        }

        using (CorrelationContext.Begin("req-42"))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://provider.example/api"));
            request.Headers.Add(CorrelationIdMiddleware.HeaderName, "provider-own");
            using (await http.SendAsync(request, ct))
            {
            }
        }

        inner.Headers.ShouldBe([null, "provider-own"]);
    }

    private static ServiceProvider Services(CapturingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(logs));
        services.AddProblemDetails();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddSingleton(new DiagnosticListener("test"));
        services.AddMetrics();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext Request(IServiceProvider services, string correlationId)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Features.Set<IHttpResponseFeature>(new StartingResponseFeature());
        context.Request.Method = "GET";
        context.Request.Path = "/api/v1/patients";
        context.Request.Headers[CorrelationIdMiddleware.HeaderName] = correlationId;
        context.Response.Body = new MemoryStream();
        return context;
    }

    /// <summary>Starts the response as a server would once the pipeline is done, running its OnStarting callbacks.</summary>
    private static Task StartResponseAsync(HttpContext context) =>
        ((StartingResponseFeature)context.Features.Get<IHttpResponseFeature>()!).StartAsync();

    private sealed class StartingResponseFeature : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _starting = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Push((callback, state));

        public async Task StartAsync()
        {
            while (_starting.TryPop(out var item))
            {
                await item.Callback(item.State);
            }
        }
    }

    private sealed record LogEntry(string Category, string? CorrelationId);

    /// <summary>Records each entry with the correlation id of the logging scope it was written in.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => provider._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                string? correlationId = null;
                provider._scopes.ForEachScope(
                    (scope, _) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object>> values
                            && values.FirstOrDefault(v => v.Key == "CorrelationId").Value is string id)
                        {
                            correlationId = id;
                        }
                    },
                    (object?)null);
                provider.Entries.Enqueue(new LogEntry(category, correlationId));
            }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string?> Headers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Headers.Add(request.Headers.TryGetValues(CorrelationIdMiddleware.HeaderName, out var values) ? values.Single() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
