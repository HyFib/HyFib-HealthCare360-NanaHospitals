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

    /// <summary>
    /// Waits until <paramref name="count"/> transactions wait on this one, directly or behind another that does, as
    /// pg_blocking_pids shows it: the requests it stopped are then caught between their reads and their writes. The probe
    /// runs each query in a transaction of its own, as PostgreSQL reads pg_stat_activity once per transaction.
    /// </summary>
    public async Task WaitUntilBlockingAsync(HospitalServer server, int count)
    {
        var held = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var holder = await held.Database.SqlQuery<int>($"""SELECT pg_backend_pid() AS "Value" """).SingleAsync(Ct);
        await using var probe = server.Services.CreateAsyncScope();
        var db = probe.ServiceProvider.GetRequiredService<HospitalDbContext>();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var waiting = await db.Database.SqlQuery<int>($"""
                WITH RECURSIVE waiting AS (
                    SELECT pid FROM pg_stat_activity WHERE {holder} = ANY(pg_blocking_pids(pid))
                    UNION
                    SELECT a.pid FROM pg_stat_activity a JOIN waiting w ON w.pid = ANY(pg_blocking_pids(a.pid))
                )
                SELECT count(*)::int AS "Value" FROM waiting
                """).SingleAsync(Ct);
            if (waiting >= count)
            {
                return;
            }

            await Task.Delay(100, Ct);
        }

        throw new ShouldAssertException($"{count} transactions did not come to wait on the test's within 10 seconds.");
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
