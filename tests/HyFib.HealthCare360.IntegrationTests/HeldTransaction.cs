using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A transaction of the test, held open with the rows it locked or wrote, to stop one side of a race at a known point:
/// released (rolled back) or committed once, then disposed.
/// </summary>
internal sealed class HeldTransaction(AsyncServiceScope scope, IDbContextTransaction transaction) : IAsyncDisposable
{
    private bool done;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Runs <paramref name="sql"/> in a transaction of the test, on its own connection, and keeps it open.</summary>
    public static async Task<HeldTransaction> HoldAsync(HospitalServer server, FormattableString sql)
    {
        var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync(sql, Ct);
        return new HeldTransaction(scope, transaction);
    }

    /// <summary>Waits until a transaction holds the lock of the visit or stay (<see cref="EncounterLocks"/>), as pg_locks shows it.</summary>
    public static async Task WaitUntilLockedAsync(HospitalServer server, Guid encounterId)
    {
        var key = (ulong)EncounterLocks.Key(encounterId);
        long high = (long)(key >> 32), low = (long)(key & 0xFFFF_FFFF);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var held = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value" FROM pg_locks
                WHERE locktype = 'advisory' AND granted AND objsubid = 1 AND classid::bigint = {high} AND objid::bigint = {low}
                  AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                """).SingleAsync(Ct);
            if (held > 0)
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new ShouldAssertException($"Nothing took the lock of {encounterId} within 10 seconds.");
    }

    public async Task ReleaseAsync()
    {
        done = true;
        await transaction.RollbackAsync(Ct);
    }

    public async Task CommitAsync()
    {
        done = true;
        await transaction.CommitAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (!done)
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await transaction.DisposeAsync();
        await scope.DisposeAsync();
    }
}
