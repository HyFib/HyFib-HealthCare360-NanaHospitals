using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

namespace HyFib.HealthCare360.Modules.Diagnostics.Persistence;

internal sealed class QcLotConfiguration : IEntityTypeConfiguration<QcLot>
{
    public void Configure(EntityTypeBuilder<QcLot> b)
    {
        b.ToTable("qc_lots", DiagnosticsSchema.Name);
        b.Property(x => x.Material).HasMaxLength(200);
        b.Property(x => x.LotNumber).HasMaxLength(64);
        b.Property(x => x.Unit).HasMaxLength(32);
        b.Property(x => x.InstrumentCode).HasMaxLength(32);
        b.Property(x => x.TargetMean).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.TargetSd).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.HasOne<LabTest>().WithMany().HasForeignKey(x => x.LabTestId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.LabTestId, x.IsActive });
    }
}

internal sealed class QcRunConfiguration : IEntityTypeConfiguration<QcRun>
{
    public void Configure(EntityTypeBuilder<QcRun> b)
    {
        b.ToTable("qc_runs", DiagnosticsSchema.Name);
        b.Property(x => x.Value).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.ZScore).HasPrecision(10, 3);
        b.Property(x => x.InstrumentCode).HasMaxLength(32);
        b.Property(x => x.ReviewNote).HasMaxLength(1000);
        b.Property(x => x.CorrectiveAction).HasMaxLength(1000);
        b.HasOne<QcLot>().WithMany().HasForeignKey(x => x.QcLotId).OnDelete(DeleteBehavior.Restrict);

        // Lockout checks read "today's latest run per lot" for a set of tests.
        b.HasIndex(x => new { x.LabTestId, x.RunAt });
        b.HasIndex(x => new { x.QcLotId, x.RunAt });
    }
}

internal sealed class QcOverrideConfiguration : IEntityTypeConfiguration<QcOverride>
{
    public void Configure(EntityTypeBuilder<QcOverride> b)
    {
        b.ToTable("qc_overrides", DiagnosticsSchema.Name);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.HasOne<QcRun>().WithMany().HasForeignKey(x => x.QcRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.LabTestId, x.OverriddenAt });
    }
}
