using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Messaging;

/// <summary>The outbox table as <see cref="OutboxDispatcher"/> works on it: one message at a time.</summary>
internal interface IOutboxStore
{
    /// <summary>
    /// The types of the waiting messages due at <paramref name="now"/>; with <paramref name="queuedAfter"/>, of those queued
    /// after it alone (while messages are held, those queued since the hold began).
    /// </summary>
    Task<IReadOnlyCollection<string>> DueTypesAsync(DateTimeOffset now, DateTimeOffset? queuedAfter, CancellationToken ct);

    /// <summary>
    /// Takes the waiting message of one of <paramref name="types"/> due longest, queued after <paramref name="queuedAfter"/>
    /// when it is given, in a transaction and service scope of its own, locked so that no other server takes it until the
    /// unit ends; null when none is due (or each due one is another server's at that moment).
    /// </summary>
    Task<IOutboxUnit?> TakeAsync(IReadOnlyCollection<string> types, DateTimeOffset now, DateTimeOffset? queuedAfter, CancellationToken ct);

    /// <summary>
    /// Ends every waiting message whose time is up, whether it is held, its provider cools down, or it was simply not
    /// reached: Expired when it was never tried, Failed when it was. Returns how many it ended.
    /// </summary>
    Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>One message taken from the outbox (<see cref="IOutboxStore.TakeAsync"/>). Disposed without a commit, it stays as it was.</summary>
internal interface IOutboxUnit : IAsyncDisposable
{
    OutboxMessage Message { get; }

    /// <summary>The unit's services: the message's handler is resolved here, and works on the unit's database context.</summary>
    IServiceProvider Services { get; }

    /// <summary>Saves the message as it stands now, with whatever its handler changed, and commits.</summary>
    Task CommitAsync(CancellationToken ct);

    /// <summary>
    /// Undoes what the handler changed, keeping the message locked, so that its outcome can be saved on its own: for when
    /// the handler left changes that cannot be saved, or a transaction a failed statement made unusable.
    /// </summary>
    Task UndoHandlerAsync(CancellationToken ct);
}

/// <summary>
/// The outbox in PostgreSQL. A message is taken with <c>FOR UPDATE SKIP LOCKED</c>, so several servers deliver side by
/// side and never the same message, and a savepoint is made before its handler runs. Its outcome is committed with what
/// its handler changed (the analysers' order queue, a message's log), in the same transaction.
/// </summary>
internal sealed class OutboxStore(IServiceScopeFactory scopes) : IOutboxStore
{
    private const string BeforeHandler = "outbox_handler";

    public async Task<IReadOnlyCollection<string>> DueTypesAsync(DateTimeOffset now, DateTimeOffset? queuedAfter, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var after = queuedAfter ?? DateTimeOffset.MinValue;
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAt <= now && m.CreatedAt > after)
            .Select(m => m.Type)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IOutboxUnit?> TakeAsync(IReadOnlyCollection<string> types, DateTimeOffset now, DateTimeOffset? queuedAfter, CancellationToken ct)
    {
        var scope = scopes.CreateAsyncScope();
        IDbContextTransaction? transaction = null;
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            transaction = await db.Database.BeginTransactionAsync(ct);
            var wanted = types.ToArray();
            var after = queuedAfter ?? DateTimeOffset.MinValue;
            var taken = await db.Set<OutboxMessage>()
                .FromSqlInterpolated($"""
                    SELECT * FROM platform.outbox_messages
                     WHERE status = 'Pending' AND next_attempt_at <= {now} AND type = ANY({wanted}) AND created_at > {after}
                     ORDER BY next_attempt_at
                     LIMIT 1
                       FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(ct);
            if (taken.Count == 0)
            {
                await transaction.DisposeAsync();
                await scope.DisposeAsync();
                return null;
            }

            await transaction.CreateSavepointAsync(BeforeHandler, ct);
            return new Unit(scope, db, transaction, taken[0]);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }

            await scope.DisposeAsync();
            throw;
        }
    }

    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var lifetime = OutboxRetryPolicy.DefaultLifetime;

        // As OutboxDispatcher.ExpireIfDue decides, in one statement; a message being delivered at this moment is left to
        // its delivery.
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE platform.outbox_messages
               SET status = CASE WHEN attempts = 0 THEN 'Expired' ELSE 'Failed' END, processed_at = {now}
             WHERE id IN (SELECT id FROM platform.outbox_messages
                           WHERE status = 'Pending' AND COALESCE(expires_at, created_at + {lifetime}) <= {now}
                             FOR UPDATE SKIP LOCKED)
            """, ct);
    }

    private sealed class Unit(AsyncServiceScope scope, HospitalDbContext db, IDbContextTransaction transaction, OutboxMessage message) : IOutboxUnit
    {
        public OutboxMessage Message => message;

        public IServiceProvider Services => scope.ServiceProvider;

        public async Task CommitAsync(CancellationToken ct)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        public async Task UndoHandlerAsync(CancellationToken ct)
        {
            await transaction.RollbackToSavepointAsync(BeforeHandler, ct);

            // Whatever the handler left behind is forgotten; the message is saved whole, with its outcome.
            db.ChangeTracker.Clear();
            db.Attach(message).State = EntityState.Modified;
        }

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await scope.DisposeAsync();
        }
    }
}
