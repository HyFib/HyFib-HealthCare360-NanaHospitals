using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Persistence;

internal sealed class NarcoticCountConfiguration : IEntityTypeConfiguration<NarcoticCount>
{
    public void Configure(EntityTypeBuilder<NarcoticCount> b)
    {
        b.ToTable("narcotic_counts", PharmacySchema.Name);
        b.Property(x => x.CountedByName).HasMaxLength(200);
        b.Property(x => x.WitnessedByName).HasMaxLength(200);
        b.Property(x => x.Explanation).HasMaxLength(1000);
        b.Ignore(x => x.HasDifference);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.NarcoticCountId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.FacilityId, x.StoreId, x.CountedAt });
    }
}

internal sealed class NarcoticCountLineConfiguration : IEntityTypeConfiguration<NarcoticCountLine>
{
    public void Configure(EntityTypeBuilder<NarcoticCountLine> b)
    {
        b.ToTable("narcotic_count_lines", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.RegisterBalance).IsQuantity();
        b.Property(x => x.Counted).IsQuantity();
        b.Ignore(x => x.Difference);
    }
}

internal sealed class AdrReportConfiguration : IEntityTypeConfiguration<AdrReport>
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AdrReport> b)
    {
        b.ToTable("adr_reports", PharmacySchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.IncidentNumber).HasMaxLength(24);
        b.Property(x => x.Reaction).HasMaxLength(2000);
        b.Property(x => x.WeightKg).HasPrecision(5, 1);
        b.Property(x => x.RelevantTests).HasMaxLength(2000);
        b.Property(x => x.MedicalHistory).HasMaxLength(2000);
        b.Property(x => x.ReporterName).HasMaxLength(200);
        b.Property(x => x.ReporterDesignation).HasMaxLength(120);
        b.Property(x => x.AssessedByName).HasMaxLength(200);
        b.Property(x => x.SubmissionReference).HasMaxLength(64);
        b.Property(x => x.Drugs)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, Json),
                v => System.Text.Json.JsonSerializer.Deserialize<List<AdrDrug>>(v, Json) ?? new List<AdrDrug>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<AdrDrug>>((x, y) => x!.SequenceEqual(y!), v => v.Count, v => v.ToList()))
            .HasColumnType("jsonb");
        b.Ignore(x => x.IsSerious);
        b.Ignore(x => x.DueBy);
        b.HasOne<HyFib.HealthCare360.Modules.Patients.Domain.Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => new { x.FacilityId, x.Status, x.LearnedOn });
    }
}
