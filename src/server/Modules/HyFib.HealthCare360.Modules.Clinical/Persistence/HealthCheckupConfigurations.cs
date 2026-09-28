using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class HealthPackageConfiguration : IEntityTypeConfiguration<HealthPackage>
{
    public void Configure(EntityTypeBuilder<HealthPackage> b)
    {
        b.ToTable("health_packages", ClinicalSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.ServiceItemId);
    }
}

internal sealed class HealthPackageItemConfiguration : IEntityTypeConfiguration<HealthPackageItem>
{
    public void Configure(EntityTypeBuilder<HealthPackageItem> b)
    {
        b.ToTable("health_package_items", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.DepartmentCode).HasMaxLength(16);
        b.HasIndex(x => x.PackageId);
    }
}

internal sealed class PackageBookingConfiguration : IEntityTypeConfiguration<PackageBooking>
{
    public void Configure(EntityTypeBuilder<PackageBooking> b)
    {
        b.ToTable("package_bookings", ClinicalSchema.Name);
        b.Property(x => x.BookingNumber).HasMaxLength(24);
        b.Property(x => x.PackageCode).HasMaxLength(32);
        b.Property(x => x.PackageName).HasMaxLength(200);
        b.Property(x => x.CorporateName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Summary).HasMaxLength(8000);
        b.Property(x => x.Recommendations).HasMaxLength(4000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<HealthPackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Itinerary).WithOne().HasForeignKey(x => x.BookingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.BookingNumber).IsUnique();
        b.HasIndex(x => new { x.ScheduledDate, x.Status });
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.CorporateBatchId);
    }
}

internal sealed class ItineraryItemConfiguration : IEntityTypeConfiguration<ItineraryItem>
{
    public void Configure(EntityTypeBuilder<ItineraryItem> b)
    {
        b.ToTable("booking_itinerary", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.DepartmentName).HasMaxLength(128);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => x.BookingId);
        b.HasIndex(x => new { x.DepartmentId, x.Status });
    }
}

internal sealed class CorporateCheckupBatchConfiguration : IEntityTypeConfiguration<CorporateCheckupBatch>
{
    public void Configure(EntityTypeBuilder<CorporateCheckupBatch> b)
    {
        b.ToTable("corporate_checkup_batches", ClinicalSchema.Name);
        b.Property(x => x.BatchNumber).HasMaxLength(24);
        b.Property(x => x.CorporateName).HasMaxLength(200);
        b.Property(x => x.ContactPerson).HasMaxLength(200);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<HealthPackage>().WithMany().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BatchNumber).IsUnique();
        b.HasIndex(x => x.ScheduledDate);
    }
}
