using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
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
}
