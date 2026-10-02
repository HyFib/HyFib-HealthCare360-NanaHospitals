using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HyFib.HealthCare360.Platform.Http;

public enum IdempotencyState
{
    InProgress = 0,
    Completed,
}

/// <summary>Stored outcome of an unsafe request, replayed when a WiFi retry repeats the same key.</summary>
[NotAudited]
public sealed class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public IdempotencyState State { get; set; }
    public int? StatusCode { get; set; }
    public string? ContentType { get; set; }
    public string? Location { get; set; }
    public byte[]? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency_records", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.Scope).HasMaxLength(128);
        b.Property(x => x.Key).HasMaxLength(128);
        b.Property(x => x.RequestHash).HasMaxLength(64);
        b.Property(x => x.Method).HasMaxLength(16);
        b.Property(x => x.Path).HasMaxLength(512);
        b.Property(x => x.ContentType).HasMaxLength(128);
        b.Property(x => x.Location).HasMaxLength(512);
        b.HasIndex(x => new { x.Scope, x.Key }).IsUnique();
        b.HasIndex(x => x.ExpiresAt);
    }
}

/// <summary>
/// Endpoint metadata: the answer must not be kept, because it is clinical text the hospital does not store (a dictation
/// transcript, an AI draft, an interpreted voice command). The <c>Idempotency-Key</c> still works: a retry with the same
/// key never runs the request again (no second AI call, no second charge to the user's AI quota). It answers 409
/// <c>idempotency.answer_not_kept</c> instead of the first answer, which was not stored, so the user asks again.
/// </summary>
public sealed class NoStoredResponseMetadata
{
    public static readonly NoStoredResponseMetadata Instance = new();

    private NoStoredResponseMetadata()
    {
    }
}

public static class IdempotencyEndpointConventions
{
    /// <summary>Keeps the endpoint's answers out of the idempotency store, retries still suppressed (see <see cref="NoStoredResponseMetadata"/>).</summary>
    public static TBuilder WithoutStoredResponse<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(NoStoredResponseMetadata.Instance);
}

/// <summary>
/// Implements the <c>Idempotency-Key</c> contract (ADR-007): the first request with a key executes and
/// its response is stored for 24 h; retries with the same key and payload get the stored response;
/// the same key with a different payload is rejected with 422. Stored responses are encrypted (AES-256-GCM,
/// key bound to the caller's scope) because they can contain one-time secrets such as temporary passwords,
/// device secrets or join links.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";
    private const int MaxStoredBodyBytes = 256 * 1024;
    private const int MaxKeyLength = 128;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly TimeSpan StaleInProgress = TimeSpan.FromMinutes(2);

    public async Task InvokeAsync(
        HttpContext context,
        IDbContextFactory<HospitalDbContext> contextFactory,
        ICurrentUser currentUser,
        IFieldProtector protector,
        TimeProvider time)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)
            || !context.Request.Headers.TryGetValue(HeaderName, out var keyValues)
            || !context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var key = keyValues.ToString().Trim();
        if (key.Length is 0 or > MaxKeyLength)
        {
            await Results.Problem(title: "Invalid Idempotency-Key header", statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
            return;
        }

        var keepsAnswer = context.GetEndpoint()?.Metadata.GetMetadata<NoStoredResponseMetadata>() is null;
        var scope = currentUser.UserId?.ToString("N") ?? "anon:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var requestHash = await HashRequestAsync(context.Request);
        var now = time.GetUtcNow();

        await using var db = await contextFactory.CreateDbContextAsync(context.RequestAborted);
        var existing = await db.Set<IdempotencyRecord>().FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key, context.RequestAborted);

        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                await Results.Problem(
                    title: "Idempotency key reused with a different request",
                    detail: "Generate a new Idempotency-Key for a new operation.",
                    statusCode: StatusCodes.Status422UnprocessableEntity).ExecuteAsync(context);
                return;
            }

            if (existing.State == IdempotencyState.Completed)
            {
                if (keepsAnswer)
                {
                    await ReplayAsync(context, existing, protector);
                }
                else
                {
                    await Results.Problem(
                        title: "This request was already answered",
                        detail: "Its answer is not kept, so it can't be sent again. Please ask again.",
                        statusCode: StatusCodes.Status409Conflict,
                        extensions: new Dictionary<string, object?> { ["code"] = "idempotency.answer_not_kept" }).ExecuteAsync(context);
                }

                return;
            }

            if (now - existing.CreatedAt < StaleInProgress)
            {
                context.Response.Headers.RetryAfter = "2";
                await Results.Problem(title: "The original request is still being processed", statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
                return;
            }

            // A crashed attempt left a stale marker: take it over.
            existing.CreatedAt = now;
            await db.SaveChangesAsync(context.RequestAborted);
        }
        else
        {
            existing = new IdempotencyRecord
            {
                Scope = scope,
                Key = key,
                RequestHash = requestHash,
                Method = context.Request.Method,
                Path = LoggedPath.Of(context),
                State = IdempotencyState.InProgress,
                CreatedAt = now,
                ExpiresAt = now + Retention,
            };
            db.Add(existing);
            try
            {
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                context.Response.Headers.RetryAfter = "2";
                await Results.Problem(title: "The original request is still being processed", statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
                return;
            }
        }

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted);

        try
        {
            if (context.Response.StatusCode >= 500)
            {
                // Server errors are not stored: the client may retry and execute again.
                db.Remove(existing);
            }
            else
            {
                existing.State = IdempotencyState.Completed;
                existing.StatusCode = context.Response.StatusCode;
                existing.ContentType = context.Response.ContentType;
                existing.Location = context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null;
                existing.ResponseBody = keepsAnswer && buffer.Length <= MaxStoredBodyBytes
                    ? protector.ProtectBytes(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), BodyPurpose(scope))
                    : null;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not persist idempotency record for key {Key}", key);
        }
    }

    private static string BodyPurpose(string scope) => $"idempotency:{scope}";

    private async Task ReplayAsync(HttpContext context, IdempotencyRecord record, IFieldProtector protector)
    {
        byte[]? body = null;
        if (record.ResponseBody is { Length: > 0 } stored)
        {
            try
            {
                body = protector.UnprotectBytes(stored, BodyPurpose(record.Scope));
            }
            catch (CryptographicException ex)
            {
                // Never replay something we cannot authenticate; the client can reload to see the outcome.
                logger.LogWarning(ex, "Stored idempotent response for key {Key} could not be decrypted", record.Key);
                await Results.Problem(
                    title: "This request was already processed",
                    detail: "Reload the screen to see the result.",
                    statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
                return;
            }
        }

        context.Response.StatusCode = record.StatusCode ?? StatusCodes.Status200OK;
        context.Response.Headers["Idempotent-Replayed"] = "true";
        if (record.Location is not null)
        {
            context.Response.Headers.Location = record.Location;
        }

        if (body is { Length: > 0 })
        {
            context.Response.ContentType = record.ContentType ?? "application/json";
            await context.Response.Body.WriteAsync(body, context.RequestAborted);
        }
    }

    private static async Task<string> HashRequestAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(System.Text.Encoding.UTF8.GetBytes($"{request.Method} {request.Path}{request.QueryString}\n"));

        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted)) > 0)
        {
            sha.AppendData(buffer, 0, read);
        }

        request.Body.Position = 0;
        return Convert.ToHexString(sha.GetHashAndReset());
    }
}

/// <summary>Deletes expired idempotency records every hour.</summary>
internal sealed class IdempotencyCleanupService(IServiceScopeFactory scopeFactory, TimeProvider time, IOptions<PlatformOptions> options, ILogger<IdempotencyCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var now = time.GetUtcNow();
                var removed = await db.Set<IdempotencyRecord>().Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(stoppingToken);
                if (removed > 0)
                {
                    logger.LogDebug("Removed {Count} expired idempotency records", removed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Idempotency cleanup failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
