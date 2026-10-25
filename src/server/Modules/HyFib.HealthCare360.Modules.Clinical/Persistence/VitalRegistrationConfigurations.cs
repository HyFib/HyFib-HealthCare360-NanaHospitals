using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class BirthRecordConfiguration : IEntityTypeConfiguration<BirthRecord>
{
    public void Configure(EntityTypeBuilder<BirthRecord> b)
    {
        b.ToTable("birth_records", ClinicalSchema.Name);
        b.Property(x => x.SerialNumber).HasMaxLength(24);
        b.Property(x => x.ChildName).HasMaxLength(200);
        b.Property(x => x.FatherName).HasMaxLength(200);
        b.Property(x => x.InformantName).HasMaxLength(200);
        b.Property(x => x.StillBirthCause).HasMaxLength(500);
        b.Property(x => x.RegistrationNumber).HasMaxLength(64);
        b.Property(x => x.ErrorReason).HasMaxLength(500);
        b.Ignore(x => x.BirthDate);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.MotherPatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.BabyPatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SerialNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.BornAt });
        b.HasIndex(x => x.MotherPatientId);
        b.HasIndex(x => x.BabyPatientId);
    }
}

internal sealed class DeathRecordConfiguration : IEntityTypeConfiguration<DeathRecord>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<DeathRecord> b)
    {
        b.ToTable("death_records", ClinicalSchema.Name);
        b.Property(x => x.SerialNumber).HasMaxLength(24);
        b.Property(x => x.Causes)
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<CauseOfDeath>>(v, Json) ?? new List<CauseOfDeath>(),
                new ValueComparer<List<CauseOfDeath>>((x, y) => x!.SequenceEqual(y!), v => v.Count, v => v.ToList()))
            .HasColumnType("jsonb");
        b.Property(x => x.OtherConditions).HasMaxLength(1000);
        b.Property(x => x.CertifiedBy).HasMaxLength(200);
        b.Property(x => x.CertifierRegistrationNumber).HasMaxLength(64);
        b.Property(x => x.RegistrationNumber).HasMaxLength(64);
        b.Property(x => x.ErrorReason).HasMaxLength(500);
        b.Ignore(x => x.DeathDate);
        b.Ignore(x => x.UnderlyingCause);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SerialNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.DiedAt });

        // One certificate per patient, unless one was entered in error.
        b.HasIndex(x => x.PatientId).IsUnique().HasFilter("status <> 'EnteredInError'");
    }
}
