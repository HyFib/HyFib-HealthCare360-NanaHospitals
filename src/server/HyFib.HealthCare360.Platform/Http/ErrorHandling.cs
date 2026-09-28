using HyFib.HealthCare360.SharedKernel;
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

/// <summary>Propagates <c>X-Correlation-Id</c> (or creates one) through logs, audit and responses.</summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = IsSafe(incoming) ? incoming : Guid.CreateVersion7().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    private static bool IsSafe(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
