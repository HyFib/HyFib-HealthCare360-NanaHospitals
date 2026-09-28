using System.Linq.Expressions;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Persistence;

/// <summary>Cross-cutting model conventions applied after every module configuration.</summary>
internal static class ModelConventions
{
    public static void Apply(ModelBuilder modelBuilder, HospitalDbContext context)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType = entityType.ClrType;
            var versioned = typeof(IVersioned).IsAssignableFrom(clrType);
            var softDeletable = typeof(ISoftDeletable).IsAssignableFrom(clrType);
            var facilityScoped = typeof(IFacilityScoped).IsAssignableFrom(clrType);

            // Only touch real entities. Calling modelBuilder.Entity() on a type EF discovered in passing (a JSON
            // list element, a complex/value type) would make it a permanent entity that then needs a key.
            if (entityType.IsOwned() || clrType.IsAbstract || !(versioned || softDeletable || facilityScoped))
            {
                continue;
            }

            var builder = modelBuilder.Entity(clrType);

            if (versioned)
            {
                // Map the optimistic-concurrency token to PostgreSQL's xmin system column.
                builder.Property(nameof(IVersioned.Version))
                    .HasColumnName("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();
            }

            var parameter = Expression.Parameter(clrType, "e");

            if (softDeletable)
            {
                var notDeleted = Expression.Not(Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted)));
                builder.HasQueryFilter(HospitalDbContext.SoftDeleteFilter, Expression.Lambda(notDeleted, parameter));
                builder.HasIndex(nameof(ISoftDeletable.IsDeleted));
            }

            if (facilityScoped)
            {
                // e => ctx.CurrentFacilityId == null || e.FacilityId == ctx.CurrentFacilityId
                // Referencing the context instance makes EF parameterise the value per query.
                var current = Expression.Property(Expression.Constant(context), nameof(HospitalDbContext.CurrentFacilityId));
                var facilityId = Expression.Convert(Expression.Property(parameter, nameof(IFacilityScoped.FacilityId)), typeof(Guid?));
                var body = Expression.OrElse(
                    Expression.Equal(current, Expression.Constant(null, typeof(Guid?))),
                    Expression.Equal(facilityId, current));
                builder.HasQueryFilter(HospitalDbContext.FacilityFilter, Expression.Lambda(body, parameter));
                builder.HasIndex(nameof(IFacilityScoped.FacilityId));
            }
        }
    }
}
