using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Persistence;

internal static class QualitySchema
{
    public const string Name = "quality";

    /// <summary>Unique indexes skip soft-deleted rows.</summary>
    public const string NotDeleted = "is_deleted = false";
}

internal sealed class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> b)
    {
        b.ToTable("incidents", QualitySchema.Name);
        b.Property(x => x.IncidentNumber).HasMaxLength(24);
        b.Property(x => x.SentinelReason).HasMaxLength(100);
        b.Property(x => x.Location).HasMaxLength(150);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.ImmediateAction).HasMaxLength(2000);
        b.Property(x => x.Witnesses).HasMaxLength(500);
        b.Property(x => x.ReporterName).HasMaxLength(200);
        b.Property(x => x.ClosureNote).HasMaxLength(2000);
        b.Property(x => x.LastStatusNote).HasMaxLength(2000);
        b.HasIndex(x => x.IncidentNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status });
        b.HasIndex(x => new { x.FacilityId, x.OccurredAt });
        b.HasIndex(x => x.ReporterUserId).HasFilter("reporter_user_id IS NOT NULL");
        b.HasIndex(x => x.PatientId).HasFilter("patient_id IS NOT NULL");
    }
}

internal sealed class InvestigationConfiguration : IEntityTypeConfiguration<Investigation>
{
    public void Configure(EntityTypeBuilder<Investigation> b)
    {
        b.ToTable("investigations", QualitySchema.Name);
        b.Property(x => x.ProblemStatement).HasMaxLength(2000);
        b.Property(x => x.Findings).HasMaxLength(4000);
        b.Property(x => x.Recommendations).HasMaxLength(4000);
        b.Ignore(x => x.IsCompleted);
        b.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Factors).WithOne().HasForeignKey(x => x.InvestigationId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.IncidentId).IsUnique().HasFilter(QualitySchema.NotDeleted);
    }
}

internal sealed class RcaFactorConfiguration : IEntityTypeConfiguration<RcaFactor>
{
    public void Configure(EntityTypeBuilder<RcaFactor> b)
    {
        b.ToTable("rca_factors", QualitySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.InvestigationId);
    }
}

internal sealed class CapaConfiguration : IEntityTypeConfiguration<Capa>
{
    public void Configure(EntityTypeBuilder<Capa> b)
    {
        b.ToTable("capas", QualitySchema.Name);
        b.Property(x => x.CapaNumber).HasMaxLength(24);
        b.Property(x => x.Action).HasMaxLength(2000);
        b.Property(x => x.CompletionNote).HasMaxLength(2000);
        b.Property(x => x.EffectivenessNote).HasMaxLength(2000);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Risk>().WithMany().HasForeignKey(x => x.RiskId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CapaNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.DueDate });
        b.HasIndex(x => new { x.OwnerUserId, x.Status });
        b.HasIndex(x => x.IncidentId);
    }
}

internal sealed class RiskConfiguration : IEntityTypeConfiguration<Risk>
{
    public void Configure(EntityTypeBuilder<Risk> b)
    {
        b.ToTable("risks", QualitySchema.Name);
        b.Property(x => x.RiskNumber).HasMaxLength(24);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.ExistingControls).HasMaxLength(2000);
        b.Property(x => x.MitigationPlan).HasMaxLength(2000);
        b.Property(x => x.LastReviewNote).HasMaxLength(1000);
        b.HasIndex(x => x.RiskNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.Score });
        b.HasIndex(x => x.ReviewDate);
    }
}

internal sealed class QualityIndicatorConfiguration : IEntityTypeConfiguration<QualityIndicator>
{
    public void Configure(EntityTypeBuilder<QualityIndicator> b)
    {
        b.ToTable("quality_indicators", QualitySchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Category).HasMaxLength(64);
        b.Property(x => x.NumeratorDefinition).HasMaxLength(500);
        b.Property(x => x.DenominatorDefinition).HasMaxLength(500);
        b.Property(x => x.NabhReference).HasMaxLength(32);
        b.Property(x => x.Multiplier).HasPrecision(12, 4);
        b.Property(x => x.Target).HasPrecision(18, 4);
        b.HasIndex(x => x.Code).IsUnique().HasFilter(QualitySchema.NotDeleted);
    }
}

internal sealed class IndicatorValueConfiguration : IEntityTypeConfiguration<IndicatorValue>
{
    public void Configure(EntityTypeBuilder<IndicatorValue> b)
    {
        b.ToTable("indicator_values", QualitySchema.Name);
        b.Property(x => x.Numerator).HasPrecision(18, 4);
        b.Property(x => x.Denominator).HasPrecision(18, 4);
        b.Property(x => x.Value).HasPrecision(18, 4);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<QualityIndicator>().WithMany().HasForeignKey(x => x.IndicatorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.IndicatorId, x.PeriodStart }).IsUnique().HasFilter(QualitySchema.NotDeleted);
    }
}
