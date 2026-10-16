using System.Diagnostics.CodeAnalysis;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// Converts unhandled exceptions into Problem Details without leaking internals. Concurrency conflicts
/// become 409; unique-constraint violations become 409 duplicates; everything else is a generic 500
/// that carries the correlation ID for support.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        IResult result;
        switch (exception)
        {
            case DbUpdateConcurrencyException:
                result = Error.ConcurrencyConflict.ToProblem();
                break;

            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg }:
                logger.LogInformation("Unique constraint {Constraint} violated", pg.ConstraintName);
                result = Error.Conflict("duplicate", "A record with the same unique value already exists.").ToProblem();
                break;

            case DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation } }:
                result = Error.Rule("reference", "This record is linked to other records and cannot be changed this way.").ToProblem();
                break;

            case BadHttpRequestException bad:
                result = TypedResults.Problem(title: "Bad request", detail: bad.Message, statusCode: bad.StatusCode);
                break;

            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                // Client went away (common on flaky WiFi); nothing to send.
                return true;

            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, LoggedPath.Of(httpContext));
                result = TypedResults.Problem(
                    title: "Something went wrong",
                    detail: "The server could not complete the request. Quote the reference ID if you contact support.",
                    statusCode: StatusCodes.Status500InternalServerError,
                    extensions: new Dictionary<string, object?> { ["referenceId"] = httpContext.TraceIdentifier });
                break;
        }

        await result.ExecuteAsync(httpContext);
        return true;
    }
}

/// <summary>
/// Propagates <c>X-Correlation-Id</c> (or creates one) through logs, audit, responses and the server's own calls to
/// outside services (<see cref="CorrelationIdForwardingHandler"/>), SRS OBS-04. It runs first, ahead of the exception
/// handler and the request log, so their entries carry the id too (<see cref="CorrelationIdApplicationBuilderExtensions"/>).
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafe(incoming) ? incoming : Guid.CreateVersion7().ToString("N");
        context.TraceIdentifier = correlationId;

        // Set as the response starts: the exception handler clears the headers before it writes the error.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (CorrelationContext.Begin(correlationId))
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    internal static bool IsSafe([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

/// <summary>
/// The correlation id of the work in hand: the request being served, or the request that queued the outbox message
/// being sent. It flows with the async call, so a background send carries the id of the request that caused it.
/// </summary>
public static class CorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    public static string? Current => CurrentId.Value;

    /// <summary>Makes <paramref name="correlationId"/> current until the result is disposed.</summary>
    public static IDisposable Begin(string? correlationId)
    {
        var previous = CurrentId.Value;
        CurrentId.Value = correlationId;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => CurrentId.Value = previous;
    }
}

/// <summary>
/// Sends the current correlation id (<see cref="CorrelationContext"/>) on the server's calls to outside services, so a
/// provider's logs or support ticket can be matched with HC360's. The id is random and says nothing about the patient
/// or the user; a request that already has one keeps it.
/// </summary>
public sealed class CorrelationIdForwardingHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var correlationId = CorrelationContext.Current;
        if (CorrelationIdMiddleware.IsSafe(correlationId) && !request.Headers.Contains(CorrelationIdMiddleware.HeaderName))
        {
            request.Headers.TryAddWithoutValidation(CorrelationIdMiddleware.HeaderName, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public static class CorrelationIdApplicationBuilderExtensions
{
    private const string Applied = "hc360.correlationId";

    /// <summary>
    /// Adds <see cref="CorrelationIdMiddleware"/> once. The host calls it ahead of its request log; the platform's
    /// pipeline calls it ahead of the exception handler, and adds it only when the host has not.
    /// </summary>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (!app.Properties.ContainsKey(Applied))
        {
            app.Properties[Applied] = true;
            app.UseMiddleware<CorrelationIdMiddleware>();
        }

        return app;
    }
}
