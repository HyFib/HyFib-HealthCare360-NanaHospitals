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
    /// <summary>The first attempt with the key is running.</summary>
    InProgress = 0,

    /// <summary>The attempt ended: its answer (status, location and, when kept, the body) is replayed to every retry.</summary>
    Completed,

    /// <summary>
    /// The attempt was still marked as running long after it began, so it is taken not to have finished (the server
    /// stopped while it ran) and nobody knows whether it changed anything: every retry is told to check instead of
    /// running it again (<see cref="IdempotencyMiddleware.OutcomeUnknownCode"/>). An attempt that was only slow records
    /// its outcome over this when it ends.
    /// </summary>
    Abandoned,
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

        // Encrypted for the user it answered, so not moved after a key rotation: it expires within a day.
        b.Property(x => x.ResponseBody).IsProtectedBytes();
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
/// <para>
/// A retry never runs a request a second time, whatever became of the first attempt (DED-08): a save whose answer was
/// lost on the WiFi, then made again, must not record the money or the stock twice.
/// </para>
/// <list type="bullet">
/// <item>The attempt runs to its end even when the client goes (a WiFi roam, a closed tab): once it has started, only its
/// end says whether it changed anything.</item>
/// <item>Its outcome is recorded before the answer is sent, so a client that has gone cannot undo it: the retry gets the
/// first answer.</item>
/// <item>An attempt that failed (an exception, or a 5xx answer) leaves nothing behind, so its retry runs again.</item>
/// <item>A retry made while the first attempt still runs gets 409 <see cref="InProgressCode"/> and Retry-After.</item>
/// <item>An attempt still marked as running long after it began did not finish: the server stopped while it ran, and it
/// may or may not have changed anything. It is marked abandoned, and every retry gets 409 <see cref="OutcomeUnknownCode"/>,
/// telling the user to check, until the record expires, or until the attempt, only slow, ends after all and records its
/// answer. It is never run again.</item>
/// </list>
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>The problem code of a retry made while the first attempt with its key still runs (409, with Retry-After).</summary>
    public const string InProgressCode = "idempotency.in_progress";

    /// <summary>The problem code of a retry whose first attempt did not finish, so it may or may not have been saved (409).</summary>
    public const string OutcomeUnknownCode = "idempotency.outcome_unknown";

    /// <summary>The problem code of a retry of a request whose answer is not kept (<see cref="NoStoredResponseMetadata"/>, 409).</summary>
    public const string AnswerNotKeptCode = "idempotency.answer_not_kept";

    private const int MaxStoredBodyBytes = 256 * 1024;
    private const int MaxKeyLength = 128;

    /// <summary>How long a retry made while the first attempt still runs is asked to wait, in seconds.</summary>
    private const string RetryAfterSeconds = "2";

    /// <summary>How often a request looks again when the record changed under it (an attempt that ended meanwhile).</summary>
    private const int MaxLookups = 3;

    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long an attempt may run before a retry takes it that the server stopped while it ran. One that was only slow
    /// still records its outcome when it ends, which the retries after that get.
    /// </summary>
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

        await using var db = await contextFactory.CreateDbContextAsync(context.RequestAborted);
        await HandleAsync(context, key, new DatabaseIdempotencyStore(db), currentUser, protector, time);
    }

    /// <summary>The contract for a request carrying <paramref name="key"/>, with its records in <paramref name="store"/>.</summary>
    internal async Task HandleAsync(HttpContext context, string key, IIdempotencyStore store, ICurrentUser currentUser, IFieldProtector protector, TimeProvider time)
    {
        var scope = currentUser.UserId?.ToString("N") ?? "anon:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        var requestHash = await HashRequestAsync(context.Request);
        var keepsAnswer = context.GetEndpoint()?.Metadata.GetMetadata<NoStoredResponseMetadata>() is null;

        if (await ClaimAsync(context, store, scope, key, requestHash, keepsAnswer, protector, time) is { } attempt)
        {
            await RunAsync(context, store, attempt, keepsAnswer, protector);
        }
    }

    /// <summary>
    /// The marker of this request as the first attempt with its key, or null when the request was answered instead: as a
    /// retry (with the first answer, or why it cannot be given), or as a key reused for another request.
    /// </summary>
    private async Task<IdempotencyRecord?> ClaimAsync(
        HttpContext context,
        IIdempotencyStore store,
        string scope,
        string key,
        string requestHash,
        bool keepsAnswer,
        IFieldProtector protector,
        TimeProvider time)
    {
        for (var lookup = 0; lookup < MaxLookups; lookup++)
        {
            var now = time.GetUtcNow();
            var existing = await store.FindAsync(scope, key, context.RequestAborted);
            if (existing is null)
            {
                var attempt = new IdempotencyRecord
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
                // Not cancelled by the client going: a marker added just as it went would stand for an attempt never run.
                if (await store.TryAddAsync(attempt, CancellationToken.None))
                {
                    return attempt;
                }

                // Another request with the key added its marker first: this one is its retry.
                continue;
            }

            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                await Results.Problem(
                    title: "Idempotency key reused with a different request",
                    detail: "Generate a new Idempotency-Key for a new operation.",
                    statusCode: StatusCodes.Status422UnprocessableEntity).ExecuteAsync(context);
                return null;
            }

            switch (existing.State)
            {
                case IdempotencyState.Completed when keepsAnswer:
                    await ReplayAsync(context, existing, protector);
                    return null;

                case IdempotencyState.Completed:
                    await ConflictAsync(
                        context,
                        AnswerNotKeptCode,
                        "This request was already answered",
                        "Its answer is not kept, so it can't be sent again. Please ask again.");
                    return null;

                case IdempotencyState.Abandoned:
                    await OutcomeUnknownAsync(context);
                    return null;

                case IdempotencyState.InProgress when now - existing.CreatedAt < StaleInProgress:
                    await InProgressAsync(context);
                    return null;
            }

            // Still marked as running long after it began: the server stopped while it ran. Whether it saved anything is not
            // known, so it is never run again; the user checks.
            if (await store.TryAbandonAsync(existing.Id, CancellationToken.None))
            {
                logger.LogWarning(
                    "The attempt of {Method} {Path} with idempotency key {Key}, begun at {Begun}, did not finish; its retries are told to check whether it was saved",
                    existing.Method,
                    existing.Path,
                    key,
                    existing.CreatedAt);
                await OutcomeUnknownAsync(context);
                return null;
            }

            // It ended, or was removed, meanwhile: look again.
        }

        // The record kept changing under this request: it is asked to come back in a moment.
        await InProgressAsync(context);
        return null;
    }

    /// <summary>Runs the request as the first attempt with its key, records its outcome, and only then sends its answer.</summary>
    private async Task RunAsync(HttpContext context, IIdempotencyStore store, IdempotencyRecord attempt, bool keepsAnswer, IFieldProtector protector)
    {
        var clientAborted = context.RequestAborted;
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        // The attempt runs to its end even if the client goes: stopped halfway, it might have saved and not said so, and
        // its answer might be cut short. Ended, its retry gets the whole answer.
        context.RequestAborted = CancellationToken.None;
        try
        {
            await next(context);
        }
        catch
        {
            // An honest failure (the exception handler answers it): nothing is kept, as for a server error, so the retry
            // runs again.
            await ForgetAsync(store, attempt);
            throw;
        }
        finally
        {
            context.RequestAborted = clientAborted;
            context.Response.Body = originalBody;
        }

        if (context.Response.StatusCode >= 500)
        {
            // Server errors are not stored: the client may retry and execute again.
            await ForgetAsync(store, attempt);
        }
        else
        {
            attempt.State = IdempotencyState.Completed;
            attempt.StatusCode = context.Response.StatusCode;
            attempt.ContentType = context.Response.ContentType;
            attempt.Location = context.Response.Headers.Location.ToString() is { Length: > 0 } location ? location : null;
            attempt.ResponseBody = keepsAnswer && buffer.Length <= MaxStoredBodyBytes
                ? protector.ProtectBytes(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), BodyPurpose(attempt.Scope))
                : null;
            await RecordAsync(store, attempt);
        }

        await SendAsync(context, buffer, clientAborted);
    }

    /// <summary>Records the attempt's outcome whatever the client does meanwhile: it is not cancelled, and a failure is logged.</summary>
    private async Task RecordAsync(IIdempotencyStore store, IdempotencyRecord attempt)
    {
        try
        {
            await store.SaveOutcomeAsync(attempt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The marker stays in progress: retries are asked to wait, then told to check whether it was saved.
            logger.LogWarning(ex, "Could not record the outcome of idempotency key {Key}", attempt.Key);
        }
    }

    /// <summary>Removes the marker of an attempt that failed, so that its retry runs again.</summary>
    private async Task ForgetAsync(IIdempotencyStore store, IdempotencyRecord attempt)
    {
        try
        {
            await store.RemoveAsync(attempt.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The marker stays in progress: retries are asked to wait, then told to check, which is safe.
            logger.LogWarning(ex, "Could not remove the marker of failed idempotency key {Key}", attempt.Key);
        }
    }

    /// <summary>
    /// Sends the buffered answer. A client that has gone does not get it, but the outcome is recorded already: its retry
    /// gets it from the store.
    /// </summary>
    private async Task SendAsync(HttpContext context, MemoryStream buffer, CancellationToken clientAborted)
    {
        buffer.Position = 0;
        try
        {
            await buffer.CopyToAsync(context.Response.Body, clientAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogDebug(ex, "The client left before the answer to {Method} {Path} reached it; a retry gets the recorded outcome", context.Request.Method, LoggedPath.Of(context));
        }
    }

    private static Task InProgressAsync(HttpContext context)
    {
        context.Response.Headers.RetryAfter = RetryAfterSeconds;
        return ConflictAsync(
            context,
            InProgressCode,
            "This request is still being processed",
            "An earlier attempt of this request has not finished yet. Wait a moment, then try again with the same details: it will not be saved twice.");
    }

    private static Task OutcomeUnknownAsync(HttpContext context) => ConflictAsync(
        context,
        OutcomeUnknownCode,
        "This may already have been saved",
        "An earlier attempt of this request did not finish, so it may or may not have been saved: check before trying again.");

    private static Task ConflictAsync(HttpContext context, string code, string title, string detail) =>
        Results.Problem(
            title: title,
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);

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

/// <summary>
/// Where <see cref="IdempotencyMiddleware"/> keeps its records: the database (<see cref="DatabaseIdempotencyStore"/>), or
/// memory in the tests. Each method is one statement, so concurrent requests with the same key see each other's changes.
/// </summary>
internal interface IIdempotencyStore
{
    /// <summary>The record of the caller's key, or null for a new key.</summary>
    Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken);

    /// <summary>Adds the marker of a first attempt; false when a request with the same key added its own first.</summary>
    Task<bool> TryAddAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Marks an attempt still in progress as abandoned; false when it is no longer in progress (it ended, or was removed).</summary>
    Task<bool> TryAbandonAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Records an attempt's outcome: its state, status, content type, location and protected answer.</summary>
    Task SaveOutcomeAsync(IdempotencyRecord record, CancellationToken cancellationToken);

    /// <summary>Removes an attempt's record, so that a retry runs again.</summary>
    Task RemoveAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>The records in <c>platform.idempotency_records</c>, written apart from the request's own unit of work.</summary>
internal sealed class DatabaseIdempotencyStore(HospitalDbContext db) : IIdempotencyStore
{
    public Task<IdempotencyRecord?> FindAsync(string scope, string key, CancellationToken cancellationToken) =>
        db.Set<IdempotencyRecord>().AsNoTracking().FirstOrDefaultAsync(r => r.Scope == scope && r.Key == key, cancellationToken);

    public async Task<bool> TryAddAsync(IdempotencyRecord record, CancellationToken cancellationToken)
    {
        db.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<bool> TryAbandonAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<IdempotencyRecord>()
            .Where(r => r.Id == id && r.State == IdempotencyState.InProgress)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, IdempotencyState.Abandoned), cancellationToken) == 1;

    public Task SaveOutcomeAsync(IdempotencyRecord record, CancellationToken cancellationToken) =>
        db.Set<IdempotencyRecord>().Where(r => r.Id == record.Id).ExecuteUpdateAsync(
            s => s
                .SetProperty(r => r.State, record.State)
                .SetProperty(r => r.StatusCode, record.StatusCode)
                .SetProperty(r => r.ContentType, record.ContentType)
                .SetProperty(r => r.Location, record.Location)
                .SetProperty(r => r.ResponseBody, record.ResponseBody),
            cancellationToken);

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken) =>
        db.Set<IdempotencyRecord>().Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken);
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
