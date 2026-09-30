using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Messaging;

public enum OutboxStatus
{
    Pending = 0,
    Succeeded,
    Failed,
    Expired,
}

/// <summary>
/// A message to deliver outside the transaction that created it (SMS, WhatsApp, e-mail, Tally, webhooks).
/// Written atomically with the business change; delivered at least once by <see cref="OutboxDispatcher"/>
/// with exponential backoff — so messages survive internet outages on the hospital WiFi link.
/// </summary>
[NotAudited]
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public OutboxStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? LastError { get; set; }
    public Guid? FacilityId { get; set; }
    public string? CorrelationId { get; set; }

    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, Outbox.JsonOptions)
        ?? throw new InvalidOperationException($"Outbox payload for '{Type}' is empty.");
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("outbox_messages", "platform");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(128);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.LastError).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(128);
        b.HasIndex(x => new { x.Status, x.NextAttemptAt });
        b.HasIndex(x => x.CreatedAt);
    }
}

public interface IOutbox
{
    /// <summary>Queues a message in the current unit of work (sent after the caller's SaveChangesAsync commits).</summary>
    void Enqueue<T>(string type, T payload, Guid? facilityId = null, TimeSpan? delay = null, TimeSpan? expiresIn = null);
}

public sealed class Outbox(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time) : IOutbox
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue<T>(string type, T payload, Guid? facilityId = null, TimeSpan? delay = null, TimeSpan? expiresIn = null)
    {
        var now = time.GetUtcNow();
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Type = type,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = now,
            NextAttemptAt = now + (delay ?? TimeSpan.Zero),
            ExpiresAt = expiresIn is { } ttl ? now + ttl : null,
            FacilityId = facilityId ?? currentUser.FacilityId,
            CorrelationId = currentUser.CorrelationId,
        });
    }
}

/// <summary>Delivers one outbox message type. Throw to trigger a retry with backoff.</summary>
public interface IOutboxHandler
{
    string Type { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>Polls pending messages (FOR UPDATE SKIP LOCKED, so several workers can run safely).</summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private const int BatchSize = 25;
    private const int MaxAttempts = 12;
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Outbox dispatch cycle failed");
            }

            if (processed < BatchSize)
            {
                await Task.Delay(IdleDelay, time, stoppingToken);
            }
        }
    }

    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToDictionary(h => h.Type, StringComparer.Ordinal);
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var batch = await db.Set<OutboxMessage>()
            .FromSqlInterpolated($"""
                SELECT * FROM platform.outbox_messages
                 WHERE status = 'Pending' AND next_attempt_at <= {now}
                 ORDER BY next_attempt_at
                 LIMIT {BatchSize}
                   FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in batch)
        {
            if (message.ExpiresAt is { } expires && expires <= now)
            {
                message.Status = OutboxStatus.Expired;
                message.ProcessedAt = now;
                continue;
            }

            if (!handlers.TryGetValue(message.Type, out var handler))
            {
                Fail(message, now, $"No handler registered for '{message.Type}'.", permanent: true);
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandlerTimeout);
            try
            {
                await handler.HandleAsync(message, timeout.Token);
                message.Status = OutboxStatus.Succeeded;
                message.ProcessedAt = time.GetUtcNow();
                message.Attempts++;
                message.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox message {MessageId} ({Type}) failed on attempt {Attempt}", message.Id, message.Type, message.Attempts + 1);
                Fail(message, now, ex.Message, permanent: false);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }

    private static void Fail(OutboxMessage message, DateTimeOffset now, string error, bool permanent)
    {
        message.Attempts++;
        message.LastError = error.Length > 2000 ? error[..2000] : error;
        if (permanent || message.Attempts >= MaxAttempts)
        {
            message.Status = OutboxStatus.Failed;
            message.ProcessedAt = now;
            return;
        }

        // 10s, 20s, 40s … capped at 1h, plus jitter so a recovered WiFi/internet link is not stampeded.
        var backoff = TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(2, message.Attempts - 1), 3600));
        message.NextAttemptAt = now + backoff + TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 3000));
    }
}
