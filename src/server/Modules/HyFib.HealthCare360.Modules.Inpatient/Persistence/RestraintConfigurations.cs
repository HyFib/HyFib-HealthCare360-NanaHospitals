using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace HyFib.HealthCare360.Modules.Inpatient.Persistence;

internal sealed class RestraintEpisodeConfiguration : IEntityTypeConfiguration<RestraintEpisode>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<RestraintEpisode> b)
    {
        b.ToTable("restraint_episodes", InpatientSchema.Name);
        b.Property(x => x.Method).HasMaxLength(300);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.AlternativesTried).HasMaxLength(1000);
        b.Property(x => x.OrderedByName).HasMaxLength(200);
        b.Property(x => x.ReleasedByName).HasMaxLength(200);
        b.Property(x => x.ReleaseReason).HasMaxLength(1000);
        b.Property(x => x.Debrief).HasMaxLength(2000);
        b.Property(x => x.DebriefedByName).HasMaxLength(200);
        b.Property(x => x.Renewals)
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<RestraintRenewal>>(v, Json) ?? new List<RestraintRenewal>(),
                new ValueComparer<List<RestraintRenewal>>((x, y) => x!.SequenceEqual(y!), v => v.Count, v => v.ToList()))
            .HasColumnType("jsonb");
        b.Ignore(x => x.IsActive);
        b.Ignore(x => x.NextCheckDue);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.OrderedAt });
        b.HasIndex(x => new { x.FacilityId, x.WardId, x.Status });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class RestraintCheckConfiguration : IEntityTypeConfiguration<RestraintCheck>
{
    public void Configure(EntityTypeBuilder<RestraintCheck> b)
    {
        b.ToTable("restraint_checks", InpatientSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CheckedByName).HasMaxLength(200);
        b.Ignore(x => x.HasConcern);
        b.HasOne<RestraintEpisode>().WithMany().HasForeignKey(x => x.EpisodeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EpisodeId, x.CheckedAt });
        b.HasIndex(x => x.PatientId);
    }
}
