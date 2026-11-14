using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
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
/// A message to deliver outside the transaction that created it (SMS, WhatsApp, e-mail, Tally, in-app alerts, webhooks).
/// Written atomically with the business change; delivered at least once by <see cref="OutboxDispatcher"/>, which
/// retries it until it is delivered or expires (<see cref="OutboxRetryPolicy"/>), so messages survive internet
/// outages on the hospital WiFi link however long they last, and an outage holds up only what goes to the internet
/// (<see cref="OutboxLane"/>).
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

/// <summary>
/// How a failed outbox message is retried (SRS DED-11: queued messages are retried until delivered or expired). The
/// wait doubles after each failure, from 10 seconds up to an hour, so a message keeps trying through an internet or
/// Tally outage of any length. It expires when its sender said (a sign-in code when the code expires, a reminder at
/// the appointment), or <see cref="DefaultLifetime"/> after it was due.
/// </summary>
public static class OutboxRetryPolicy
{
    /// <summary>How long a message whose sender set no expiry keeps being tried.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(7);

    /// <summary>The longest wait between two tries.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromHours(1);

    /// <summary>
    /// The wait after a message's <paramref name="failures"/>th failure: 10 s, 20 s, 40 s … then an hour.
    /// </summary>
    public static TimeSpan WaitAfter(int failures) =>
        TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(2, Math.Max(failures, 1) - 1), LongestWait.TotalSeconds));

    /// <summary>
    /// When the message stops being tried. Messages queued without an expiry by older versions get the default.
    /// </summary>
    public static DateTimeOffset ExpiryOf(OutboxMessage message) =>
        message.ExpiresAt ?? message.CreatedAt + DefaultLifetime;
}

public interface IOutbox
{
    /// <summary>
    /// Queues a message in the current unit of work (sent after the caller's SaveChangesAsync commits). It is retried
    /// until it is delivered or <paramref name="expiresIn"/> has passed; without one it is tried for
    /// <see cref="OutboxRetryPolicy.DefaultLifetime"/> from when it is due.
    /// </summary>
    void Enqueue<T>(string type, T payload, Guid? facilityId = null, TimeSpan? delay = null, TimeSpan? expiresIn = null);
}

public sealed class Outbox(HospitalDbContext db, ICurrentUser currentUser, TimeProvider time) : IOutbox
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue<T>(string type, T payload, Guid? facilityId = null, TimeSpan? delay = null, TimeSpan? expiresIn = null)
    {
        var now = time.GetUtcNow();
        var due = now + (delay ?? TimeSpan.Zero);
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Type = type,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = now,
            NextAttemptAt = due,
            ExpiresAt = expiresIn is { } ttl ? now + ttl : due + OutboxRetryPolicy.DefaultLifetime,
            FacilityId = facilityId ?? currentUser.FacilityId,
            CorrelationId = currentUser.CorrelationId,
        });
    }
}

/// <summary>
/// Delivers one outbox message type. Throw to have the message tried again with back-off, and throw
/// <see cref="ProviderUnreachableException"/> when the provider could not be reached or did not answer in time.
/// </summary>
public interface IOutboxHandler
{
    string Type { get; }

    /// <summary>
    /// Where the delivery goes: the internet, unless the handler opts in to the local lane because it stays inside the
    /// hospital (an in-app or realtime notification, work in the database, a system on the LAN). A local delivery is held
    /// up neither by an internet outage nor by the hold after a restore (<see cref="OutboxHold"/>), so it must call nothing
    /// on the internet: what it queues to go there, such as an alert's SMS copy, is a message of its own on the internet lane.
    /// </summary>
    OutboxLane Lane => OutboxLane.Internet;

    /// <summary>
    /// The outside service the delivery calls, the same for every handler that calls it (<see cref="OutboxProviders"/>):
    /// a provider's messages go one at a time, beside every other provider's, and its connection failures and timeouts
    /// count together towards its cool-down (<see cref="OutboxCoolDowns"/>). The message type by default.
    /// </summary>
    string Provider => Type;

    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>The channel providers of the notification handlers, for <see cref="IOutboxHandler.Provider"/>.</summary>
public static class OutboxProviders
{
    public const string Sms = "sms";

    public const string WhatsApp = "whatsapp";

    public const string Email = "email";
}

/// <summary>What the dispatcher knows of a handler without running it: its message type, lane and provider.</summary>
internal sealed record OutboxHandlerInfo(string Type, OutboxLane Lane, string Provider)
{
    public static OutboxHandlerInfo Of(IOutboxHandler handler) =>
        new(handler.Type, handler.Lane, string.IsNullOrEmpty(handler.Provider) ? handler.Type : handler.Provider);
}

/// <summary>A provider the outbox delivers to (<see cref="IOutboxHandler.Provider"/>): the message types its handlers deliver, and their lane.</summary>
internal sealed record OutboxProvider(string Name, OutboxLane Lane, IReadOnlyList<string> Types)
{
    /// <summary>
    /// The providers of the message types due, from the handlers by message type. A provider is on the internet lane when
    /// any of its handlers is. A type that no handler delivers is a provider of its own on the internet lane, and its
    /// messages fail at once.
    /// </summary>
    public static IReadOnlyList<OutboxProvider> Of(IReadOnlyCollection<string> dueTypes, IReadOnlyDictionary<string, OutboxHandlerInfo> handlers)
    {
        var providers = new Dictionary<string, OutboxProvider>(StringComparer.Ordinal);
        foreach (var type in dueTypes)
        {
            if (!handlers.TryGetValue(type, out var handler))
            {
                providers.TryAdd(type, new OutboxProvider(type, OutboxLane.Internet, [type]));
            }
            else if (!providers.ContainsKey(handler.Provider))
            {
                var theirs = handlers.Values.Where(h => string.Equals(h.Provider, handler.Provider, StringComparison.Ordinal)).ToList();
                providers[handler.Provider] = new OutboxProvider(
                    handler.Provider, theirs.TrueForAll(h => h.Lane == OutboxLane.Local) ? OutboxLane.Local : OutboxLane.Internet, [.. theirs.Select(h => h.Type)]);
            }
        }

        return [.. providers.Values];
    }

    /// <summary>
    /// The message types on the local lane, as <see cref="Of"/> decides: those of the providers whose every handler is
    /// local. Every other type, one no handler delivers included, is on the internet lane.
    /// </summary>
    public static IReadOnlyList<string> LocalTypes(IEnumerable<IOutboxHandler> handlers) =>
        [.. handlers.Select(OutboxHandlerInfo.Of)
            .GroupBy(h => h.Provider, StringComparer.Ordinal)
            .Where(provider => provider.All(h => h.Lane == OutboxLane.Local))
            .SelectMany(provider => provider.Select(h => h.Type))];
}

/// <summary>
/// Delivers the outbox (SRS DED-11) so that an internet outage holds up only what goes to the internet.
/// <list type="bullet">
/// <item>Each message is taken on its own, in a transaction of its own, locked with <c>FOR UPDATE SKIP LOCKED</c> so that
/// several servers deliver side by side and never the same message, and its outcome is committed before the next is
/// taken: a restart part way through sends again at most the one message it was sending.</item>
/// <item>Messages go provider by provider (<see cref="IOutboxHandler.Provider"/>): each provider's one at a time, in the
/// order they fell due, and the providers side by side. An SMS gateway that hangs holds up only the SMS, and a fridge's
/// alert in the app, on the local lane (<see cref="OutboxLane"/>), goes within seconds whatever the internet does.</item>
/// <item>After <see cref="OutboxCoolDowns.FailuresBeforeCoolDown"/> connection failures or timeouts in a row, a provider's
/// messages wait out a cool-down without being tried (<see cref="OutboxCoolDowns"/>).</item>
/// <item>A failed message's next try is counted from when the try failed (<see cref="OutboxRetryPolicy"/>).</item>
/// <item>While outgoing messages are held after a restore (<see cref="OutboxHold"/>), the internet lane sends none of those
/// queued before the hold began; those queued since go as ever.</item>
/// <item>Once a minute, every waiting message whose time is up is ended, held or not (<see cref="ExpireIfDue"/>).</item>
/// </list>
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    /// <summary>How long a handler has to deliver one message.</summary>
    internal static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How often the waiting messages whose time is up are ended.</summary>
    internal static readonly TimeSpan ExpiryInterval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOutboxStore _store;
    private readonly TimeProvider _time;
    private readonly IOptions<PlatformOptions> _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private IReadOnlyDictionary<string, OutboxHandlerInfo>? _handlers;
    private DateTimeOffset _nextExpiry = DateTimeOffset.MinValue;
    private bool _held;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory, TimeProvider time, IOptions<PlatformOptions> options, ILogger<OutboxDispatcher> logger)
        : this(scopeFactory, new OutboxStore(scopeFactory), time, options, logger)
    {
    }

    internal OutboxDispatcher(
        IServiceScopeFactory scopeFactory, IOutboxStore store, TimeProvider time, IOptions<PlatformOptions> options, ILogger<OutboxDispatcher> logger)
    {
        _scopes = scopeFactory;
        _store = store;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>Each provider's run of failures that got no answer, and its cool-down.</summary>
    internal OutboxCoolDowns CoolDowns { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.RunsJobs)
        {
            return;
        }

        // Each provider's delivery while it runs: one at a time per provider, as many providers at once as have messages due.
        var deliveries = new Dictionary<string, Task>(StringComparer.Ordinal);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                foreach (var done in deliveries.Where(d => d.Value.IsCompleted).Select(d => d.Key).ToList())
                {
                    deliveries.Remove(done);
                }

                try
                {
                    foreach (var provider in await DueAsync(deliveries.Keys, stoppingToken))
                    {
                        deliveries[provider.Name] = DeliverAsync(provider, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Outbox dispatch cycle failed");
                }

                await Task.Delay(IdleDelay, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Each delivery stops at its next message, or when the handler it waits on gives up on the stop.
            await Task.WhenAll(deliveries.Values);
        }
    }

    /// <summary>One round, for the tests: delivers what is due, the providers side by side, and waits until each has delivered what it can.</summary>
    internal async Task DispatchOnceAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll((await DueAsync([], cancellationToken)).Select(provider => DeliverAsync(provider, cancellationToken)));

    /// <summary>
    /// Ends the waiting messages whose time is up, once a minute, and returns the providers with messages due that have no
    /// delivery running here (<paramref name="running"/>) and are not cooling down. While messages are held, a provider on
    /// the internet lane is due only for messages queued since the hold began.
    /// </summary>
    internal async Task<IReadOnlyList<OutboxProvider>> DueAsync(ICollection<string> running, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now >= _nextExpiry)
        {
            _nextExpiry = now + ExpiryInterval;
            if (await _store.ExpireAsync(now, cancellationToken) is var ended and > 0)
            {
                _logger.LogWarning("{Count} outbox message(s) ended unsent: their time was up before they could go", ended);
            }
        }

        var providers = OutboxProvider.Of(await _store.DueTypesAsync(now, null, cancellationToken), await HandlersAsync())
            .Where(p => !running.Contains(p.Name) && CoolDowns.CoolingUntil(p.Name, now) is null)
            .ToList();
        if (providers.Exists(p => p.Lane == OutboxLane.Internet))
        {
            var hold = await HoldAsync(cancellationToken);
            if (hold is not null)
            {
                // A hold that cannot be read holds every message; else only those queued before it began.
                IReadOnlyCollection<string> queuedSince = hold.Since == default ? [] : await _store.DueTypesAsync(now, hold.Since, cancellationToken);
                providers.RemoveAll(p => p.Lane == OutboxLane.Internet && !p.Types.Any(queuedSince.Contains));
            }

            if (hold is not null != _held)
            {
                _held = hold is not null;
                if (hold is not null)
                {
                    _logger.LogWarning("Outgoing messages queued before {Since:O} are held: they go to the internet once an administrator releases them", hold.Since);
                }
                else
                {
                    _logger.LogWarning("Outgoing messages are no longer held: they go to the internet again");
                }
            }
        }

        return providers;
    }

    /// <summary>
    /// Takes the provider's next due message, delivers it and commits its outcome. False when there was none to take. While
    /// messages are held, the internet lane takes only those queued since the hold began: the others stay as they were.
    /// </summary>
    internal async Task<bool> ProcessNextAsync(OutboxProvider provider, CancellationToken stoppingToken)
    {
        // Asked for each message: the hold may come on while a provider's messages are going.
        DateTimeOffset? queuedAfter = null;
        if (provider.Lane == OutboxLane.Internet && await HoldAsync(stoppingToken) is { } hold)
        {
            if (hold.Since == default)
            {
                return false;
            }

            queuedAfter = hold.Since;
        }

        var now = _time.GetUtcNow();
        await using var unit = await _store.TakeAsync(provider.Types, now, queuedAfter, stoppingToken);
        if (unit is null)
        {
            return false;
        }

        var message = unit.Message;
        if (ExpireIfDue(message, now))
        {
            await unit.CommitAsync(stoppingToken);
            return true;
        }

        var handler = unit.Services.GetServices<IOutboxHandler>().FirstOrDefault(h => string.Equals(h.Type, message.Type, StringComparison.Ordinal));
        if (handler is null)
        {
            Fail(message, now, $"No handler registered for '{message.Type}'.", permanent: true);
            await unit.CommitAsync(stoppingToken);
            return true;
        }

        var attempts = message.Attempts;
        var failure = await HandleAsync(handler, message, stoppingToken);

        // The next try is counted from when this one ended, however long it took. Once the handler has run, its outcome
        // is committed even while the server stops: abandoned, it would be sent again after the restart.
        var at = _time.GetUtcNow();
        Record(message, failure, at);
        try
        {
            await unit.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // What the handler changed cannot be saved, or a failed statement left the transaction unusable: the outcome is
            // saved on its own. A delivery whose changes are lost is tried again, as one that failed.
            _logger.LogWarning(ex, "The outcome of outbox message {MessageId} ({Type}) could not be saved with what its handler changed", message.Id, message.Type);
            await unit.UndoHandlerAsync(CancellationToken.None);
            if (failure is null)
            {
                message.Status = OutboxStatus.Pending;
                message.ProcessedAt = null;
                message.Attempts = attempts;
                failure = new DeliveryFailure($"It was delivered, but what the delivery changed could not be saved: {ex.Message}", Unreachable: false);
                Record(message, failure, at);
            }

            await unit.CommitAsync(CancellationToken.None);
        }

        Count(provider, failure, at);
        return true;
    }

    /// <summary>
    /// Ends a message whose time is up: Expired when it was never tried (it was no longer worth sending by the time it
    /// could go, after the server was off, say), Failed when it kept failing until then.
    /// </summary>
    internal static bool ExpireIfDue(OutboxMessage message, DateTimeOffset now)
    {
        if (OutboxRetryPolicy.ExpiryOf(message) > now)
        {
            return false;
        }

        message.Status = message.Attempts == 0 ? OutboxStatus.Expired : OutboxStatus.Failed;
        message.ProcessedAt = now;
        return true;
    }

    /// <summary>
    /// Records a failed try and when to try again. A permanent failure, or one whose next try would come after the
    /// message expires, ends it as Failed.
    /// </summary>
    internal static void Fail(OutboxMessage message, DateTimeOffset now, string error, bool permanent, TimeSpan jitter)
    {
        message.Attempts++;
        error = SensitiveText.Redact(error);
        message.LastError = error.Length > 2000 ? error[..2000] : error;
        var next = now + OutboxRetryPolicy.WaitAfter(message.Attempts) + jitter;
        if (permanent || next >= OutboxRetryPolicy.ExpiryOf(message))
        {
            message.Status = OutboxStatus.Failed;
            message.ProcessedAt = now;
            return;
        }

        message.NextAttemptAt = next;
    }

    private static void Fail(OutboxMessage message, DateTimeOffset now, string error, bool permanent) =>
        // Jitter, so that a recovered WiFi or internet link is not stampeded.
        Fail(message, now, error, permanent, TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, 3000)));

    private static void Record(OutboxMessage message, DeliveryFailure? failure, DateTimeOffset at)
    {
        if (failure is not null)
        {
            Fail(message, at, failure.Error, permanent: false);
            return;
        }

        message.Status = OutboxStatus.Succeeded;
        message.ProcessedAt = at;
        message.Attempts++;
        message.LastError = null;
    }

    /// <summary>Delivers the provider's due messages one at a time, until none is due, the provider cools down, or the hold comes on.</summary>
    private async Task DeliverAsync(OutboxProvider provider, CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested
                   && CoolDowns.CoolingUntil(provider.Name, _time.GetUtcNow()) is null
                   && await ProcessNextAsync(provider, stoppingToken))
            {
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the message being delivered stays waiting, and goes after the restart.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outbox delivery to {Provider} failed", provider.Name);
        }
    }

    /// <summary>Runs the handler on the message within <see cref="HandlerTimeout"/>: null when it delivered it, else why not.</summary>
    private async Task<DeliveryFailure?> HandleAsync(IOutboxHandler handler, OutboxMessage message, CancellationToken stoppingToken)
    {
        using var limit = new CancellationTokenSource(HandlerTimeout, _time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, limit.Token);

        // The send carries the id of the request that queued it, in the log and to the provider (OBS-04).
        using var correlation = CorrelationContext.Begin(message.CorrelationId);
        using var logScope = message.CorrelationId is null
            ? null
            : _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = message.CorrelationId });
        try
        {
            await handler.HandleAsync(message, timeout.Token);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Outbox message {MessageId} ({Type}) failed on attempt {Attempt}", message.Id, message.Type, message.Attempts + 1);
            return ex is OperationCanceledException && limit.IsCancellationRequested
                ? new DeliveryFailure(string.Create(CultureInfo.InvariantCulture, $"The delivery did not finish within {HandlerTimeout.TotalSeconds:0} s."), Unreachable: true)
                : new DeliveryFailure(ex.Message, OutboxFailures.IsUnreachable(ex));
        }
    }

    /// <summary>Counts the delivery towards its provider's cool-down.</summary>
    private void Count(OutboxProvider provider, DeliveryFailure? failure, DateTimeOffset at)
    {
        if (failure is not { Unreachable: true })
        {
            CoolDowns.Answered(provider.Name);
        }
        else if (CoolDowns.Unreachable(provider.Name, at) is { } until)
        {
            _logger.LogWarning(
                "{Provider} could not be reached {Failures} or more times in a row: its messages wait until {Until:O} before the next try",
                provider.Name, OutboxCoolDowns.FailuresBeforeCoolDown, until);
        }
    }

    /// <summary>The handlers by message type, read once: their types, lanes and providers do not change while the server runs.</summary>
    private async Task<IReadOnlyDictionary<string, OutboxHandlerInfo>> HandlersAsync()
    {
        if (_handlers is null)
        {
            await using var scope = _scopes.CreateAsyncScope();
            _handlers = scope.ServiceProvider.GetServices<IOutboxHandler>().ToDictionary(h => h.Type, OutboxHandlerInfo.Of, StringComparer.Ordinal);
        }

        return _handlers;
    }

    private async Task<OutboxHold?> HoldAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        return await OutboxHolds.ReadAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), cancellationToken);
    }

    /// <summary>Why a delivery failed, and whether for want of an answer from its provider (<see cref="OutboxFailures.IsUnreachable"/>).</summary>
    private sealed record DeliveryFailure(string Error, bool Unreachable);
}
