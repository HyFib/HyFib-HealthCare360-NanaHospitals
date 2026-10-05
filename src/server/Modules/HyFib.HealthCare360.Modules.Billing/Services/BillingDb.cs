using Microsoft.EntityFrameworkCore.Storage;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Unit-of-work helpers shared by the billing services.</summary>
internal static class BillingDb
{
    public static Error FacilityRequired { get; } = Error.Rule("facility.required", "Select a facility first.");

    public static Error SignInRequired { get; } = Error.Forbidden("auth.required", "Sign in first.");

    /// <summary>
    /// Another writer saved first: a row with the same unique key, or the row changed since it was read (xmin).
    /// </summary>
    public static bool IsConflict(DbUpdateException exception) =>
        exception is DbUpdateConcurrencyException
        || exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Starts a transaction unless the caller already runs inside one. Document numbers are allocated inside it, so a
    /// failed save rolls the number back and every series stays gap-free.
    /// </summary>
    public static async Task<OwnedTransaction> BeginOwnedTransactionAsync(this HospitalDbContext db, CancellationToken ct) =>
        new(db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null);

    /// <summary>
    /// Applies the client's last-seen version and forces the root row to be written, so a concurrent change is detected
    /// even when a request only touches child rows (bill lines, deductions, inclusions).
    /// </summary>
    public static void GuardVersion<T>(this HospitalDbContext db, T entity, uint expectedVersion)
        where T : AuditableEntity
    {
        db.SetExpectedVersion(entity, expectedVersion);
        db.Entry(entity).Property(nameof(IAuditable.UpdatedAt)).IsModified = true;
    }
}

/// <summary>A transaction that is committed/disposed only by the code that started it.</summary>
internal sealed class OwnedTransaction(IDbContextTransaction? transaction) : IAsyncDisposable
{
    public Task CommitAsync(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
}
