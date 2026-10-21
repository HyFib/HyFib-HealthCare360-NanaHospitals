using System.Data.Common;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>
/// The single unit of work for the whole hospital database. Each module contributes its own
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes (one PostgreSQL schema per module);
/// entities are accessed with <c>db.Set&lt;T&gt;()</c>.
/// </summary>
public sealed class HospitalDbContext : DbContext
{
    public const string SoftDeleteFilter = "soft_delete";
    public const string FacilityFilter = "facility";

    private readonly ModuleCatalog _modules;
    private readonly ICurrentUser? _currentUser;
    private List<Action>? _afterCommit;

    public HospitalDbContext(DbContextOptions<HospitalDbContext> options, ModuleCatalog modules, ICurrentUser? currentUser = null)
        : base(options)
    {
        _modules = modules;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Facility applied by the named <see cref="FacilityFilter"/> query filter. <c>null</c> (system jobs,
    /// anonymous requests) disables facility scoping, so anonymous endpoints must filter explicitly.
    /// </summary>
    public Guid? CurrentFacilityId => _currentUser?.FacilityId;

    /// <summary>
    /// Runs <paramref name="action"/> once what this context changed is committed: at once when no transaction is open on
    /// it (SaveChanges, or the statement, committed already), else when the open one commits, and never when it is rolled
    /// back. For telling others of a change (expiring a cache here and on the other servers): told before the commit, a
    /// reader would read the old value meanwhile and keep it, with nothing left to expire it.
    /// </summary>
    public void AfterCommit(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Database.CurrentTransaction is null)
        {
            action();
            return;
        }

        (_afterCommit ??= []).Add(action);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(TransactionEnds.Instance);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

        foreach (var assembly in _modules.Assemblies)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(assembly);
        }

        ModelConventions.Apply(modelBuilder, this);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored as readable strings (reporting/BI friendly and safe to reorder).
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(64);
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

        // PostgreSQL timestamptz accepts only UTC offsets; normalise anything a client sends.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();

        // Value objects are stored inline (address_line1, address_city…) wherever they appear.
        configurationBuilder.ComplexProperties<SharedKernel.Address>();
    }

    private sealed class UtcDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
        v => v.ToUniversalTime(),
        v => v);

    private void Committed()
    {
        var actions = _afterCommit;
        _afterCommit = null;
        foreach (var action in actions ?? [])
        {
            action();
        }
    }

    /// <summary>Runs <see cref="AfterCommit"/>'s actions when the transaction commits, and forgets them otherwise.</summary>
    private sealed class TransactionEnds : DbTransactionInterceptor
    {
        public static readonly TransactionEnds Instance = new();

        public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
        {
            Forget(eventData);
            return result;
        }

        public override ValueTask<DbTransaction> TransactionStartedAsync(
            DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            // Left by a transaction disposed without a commit or a rollback, which no interceptor hears.
            Forget(eventData);
            return ValueTask.FromResult(result);
        }

        public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
            (eventData.Context as HospitalDbContext)?.Committed();

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            (eventData.Context as HospitalDbContext)?.Committed();
            return Task.CompletedTask;
        }

        public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => Forget(eventData);

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Forget(eventData);
            return Task.CompletedTask;
        }

        public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData) => Forget(eventData);

        public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Forget(eventData);
            return Task.CompletedTask;
        }

        private static void Forget(TransactionEventData eventData)
        {
            if (eventData.Context is HospitalDbContext db)
            {
                db._afterCommit = null;
            }
        }
    }
}
