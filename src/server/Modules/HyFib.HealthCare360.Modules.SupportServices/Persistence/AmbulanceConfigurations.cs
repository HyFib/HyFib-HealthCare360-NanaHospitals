using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class AmbulanceVehicleConfiguration : IEntityTypeConfiguration<AmbulanceVehicle>
{
    public void Configure(EntityTypeBuilder<AmbulanceVehicle> b)
    {
        b.ToTable("ambulance_vehicles", SupportServicesSchema.Name);
        b.Property(x => x.RegistrationNumber).HasMaxLength(20);
        b.HasIndex(x => x.RegistrationNumber).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class AmbulanceTripConfiguration : IEntityTypeConfiguration<AmbulanceTrip>
{
    public void Configure(EntityTypeBuilder<AmbulanceTrip> b)
    {
        b.ToTable("ambulance_trips", SupportServicesSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.PatientName).HasMaxLength(200);
        b.Property(x => x.CallerName).HasMaxLength(200);
        b.Property(x => x.CallerPhone).HasMaxLength(16);
        b.Property(x => x.PickupAddress).HasMaxLength(500);
        b.Property(x => x.DropAddress).HasMaxLength(500);
        b.Property(x => x.Complaint).HasMaxLength(500);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.VehicleNumber).HasMaxLength(20);
        b.Property(x => x.DriverName).HasMaxLength(200);
        b.Property(x => x.CrewName).HasMaxLength(200);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.ChargeWaivedReason).HasMaxLength(300);
        b.Ignore(x => x.Kilometres);
        b.Ignore(x => x.IsActive);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AmbulanceVehicle>().WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.RequestedAt });
        b.HasIndex(x => x.PatientId);

        // A vehicle is out on one trip at a time.
        b.HasIndex(x => x.VehicleId).IsUnique()
            .HasFilter("vehicle_id IS NOT NULL AND status IN ('Dispatched', 'AtScene', 'Transporting') AND is_deleted = false");
    }
}
