using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class DietTypeConfiguration : IEntityTypeConfiguration<DietType>
{
    public void Configure(EntityTypeBuilder<DietType> b)
    {
        b.ToTable("diet_types", SupportServicesSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(500);
        // Flags are stored as their integer value so any combination of meal rounds round-trips.
        b.Property(x => x.DefaultMealSlots).HasConversion<int>();
        b.Ignore(x => x.IsNilByMouth);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.ChargeServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class DietOrderConfiguration : IEntityTypeConfiguration<DietOrder>
{
    public void Configure(EntityTypeBuilder<DietOrder> b)
    {
        b.ToTable("diet_orders", SupportServicesSchema.Name);
        b.Property(x => x.AllergyNotes).HasMaxLength(500);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.Property(x => x.OrderedByName).HasMaxLength(200);
        b.Property(x => x.StopReason).HasMaxLength(300);
        b.Property(x => x.MealSlots).HasConversion<int>();
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DietType>().WithMany().HasForeignKey(x => x.DietTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.Status });
        b.HasIndex(x => new { x.Status, x.StartDate });
        b.HasIndex(x => x.PatientId);
        // Idempotent handling of clinical diet orders. "One active diet per admission" is enforced by superseding in code,
        // not by a unique index: a supersede and its replacement are saved together inside event handlers.
        b.HasIndex(x => x.ServiceOrderItemId).IsUnique().HasFilter("service_order_item_id IS NOT NULL");
    }
}

internal sealed class PlannedMealConfiguration : IEntityTypeConfiguration<PlannedMeal>
{
    public void Configure(EntityTypeBuilder<PlannedMeal> b)
    {
        b.ToTable("planned_meals", SupportServicesSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.BedLabel).HasMaxLength(24);
        b.Property(x => x.DietCode).HasMaxLength(24);
        b.Property(x => x.DietName).HasMaxLength(100);
        b.Property(x => x.AllergyNotes).HasMaxLength(500);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.Property(x => x.DeliveredByName).HasMaxLength(200);
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasOne<DietOrder>().WithMany().HasForeignKey(x => x.DietOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PlanDate, x.Slot, x.WardId });
        b.HasIndex(x => new { x.AdmissionId, x.PlanDate });
        b.HasIndex(x => new { x.DietOrderId, x.PlanDate, x.Slot }).IsUnique();
    }
}
