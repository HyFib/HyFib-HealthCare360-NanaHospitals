using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Domain.Audits;
using HyFib.HealthCare360.Modules.Quality.Domain.Hai;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HyFib.HealthCare360.Modules.Quality.Persistence;

internal sealed class DeviceEpisodeConfiguration : IEntityTypeConfiguration<DeviceEpisode>
{
    public void Configure(EntityTypeBuilder<DeviceEpisode> b)
    {
        b.ToTable("device_episodes", QualitySchema.Name);
        b.Property(x => x.Detail).HasMaxLength(200);
        b.Property(x => x.InsertedByName).HasMaxLength(200);
        b.Property(x => x.RemovedByName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Ignore(x => x.InPlace);
        b.Ignore(x => x.IsExtubation);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Device, x.InsertedAt });
        b.HasIndex(x => new { x.PatientId, x.Device });
        b.HasIndex(x => x.AdmissionId);
    }
}

internal sealed class HaiCaseConfiguration : IEntityTypeConfiguration<HaiCase>
{
    public void Configure(EntityTypeBuilder<HaiCase> b)
    {
        b.ToTable("hai_cases", QualitySchema.Name);
        b.Property(x => x.CaseNumber).HasMaxLength(24);
        b.Property(x => x.Organism).HasMaxLength(200);
        b.Property(x => x.Specimen).HasMaxLength(200);
        b.Property(x => x.SurgeryReference).HasMaxLength(200);
        b.Property(x => x.ReviewedByName).HasMaxLength(200);
        b.Property(x => x.DecisionNote).HasMaxLength(1000);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Ignore(x => x.DaysAfterOperation);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DeviceEpisode>().WithMany().HasForeignKey(x => x.DeviceEpisodeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CaseNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.EventOn });
        b.HasIndex(x => new { x.FacilityId, x.Type, x.EventOn });
        b.HasIndex(x => x.PatientId);

        // A culture raises a case of a type once, however often the scan sees it.
        b.HasIndex(x => new { x.CultureResultId, x.Type }).IsUnique().HasFilter("culture_result_id IS NOT NULL");
    }
}

internal sealed class AuditTemplateConfiguration : IEntityTypeConfiguration<AuditTemplate>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AuditTemplate> b)
    {
        b.ToTable("audit_templates", QualitySchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Items)
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<AuditItem>>(v, Json) ?? new List<AuditItem>(),
                new ValueComparer<List<AuditItem>>((x, y) => x!.SequenceEqual(y!), v => v.Aggregate(0, (h, i) => HashCode.Combine(h, i.GetHashCode())), v => v.ToList()))
            .HasColumnType("jsonb");
        b.HasIndex(x => x.Code).IsUnique().HasFilter(QualitySchema.NotDeleted);
    }
}

internal sealed class QualityAuditConfiguration : IEntityTypeConfiguration<QualityAudit>
{
    public void Configure(EntityTypeBuilder<QualityAudit> b)
    {
        b.ToTable("audits", QualitySchema.Name);
        b.Property(x => x.AuditNumber).HasMaxLength(24);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Area).HasMaxLength(150);
        b.Property(x => x.Summary).HasMaxLength(2000);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.HasOne<AuditTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Findings).WithOne().HasForeignKey(x => x.AuditId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.AuditNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.ScheduledOn });
        b.HasIndex(x => x.LeadAuditorUserId);
    }
}

internal sealed class AuditFindingConfiguration : IEntityTypeConfiguration<AuditFinding>
{
    public void Configure(EntityTypeBuilder<AuditFinding> b)
    {
        b.ToTable("audit_findings", QualitySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemKey).HasMaxLength(32);
        b.Property(x => x.ItemText).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(32);
        b.Property(x => x.Observation).HasMaxLength(2000);
        b.HasOne<Capa>().WithMany().HasForeignKey(x => x.CapaId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AuditId, x.ItemKey }).IsUnique();
        b.HasIndex(x => x.CapaId).HasFilter("capa_id IS NOT NULL");
    }
}
