using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class DoctorShareRuleConfiguration : IEntityTypeConfiguration<DoctorShareRule>
{
    public void Configure(EntityTypeBuilder<DoctorShareRule> b)
    {
        b.ToTable("doctor_share_rules", BillingSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => new { x.PractitionerId, x.IsActive });
    }
}

internal sealed class DoctorFeeAccrualConfiguration : IEntityTypeConfiguration<DoctorFeeAccrual>
{
    public void Configure(EntityTypeBuilder<DoctorFeeAccrual> b)
    {
        b.ToTable("doctor_fee_accruals", BillingSchema.Name);
        b.Property(x => x.BillNumber).HasMaxLength(24);
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.Description).HasMaxLength(300);

        // One accrual per bill line and at most one reversal per accrual, so re-dispatched events never double-pay.
        // One accrual of a line at a time: the next occurrence only after the one before was reversed.
        b.HasIndex(x => new { x.BillLineId, x.Occurrence }, "ix_doctor_fee_accruals_one_per_line").IsUnique().HasFilter("reversal_of_id IS NULL");
        b.HasIndex(x => x.ReversalOfId).IsUnique().HasFilter("reversal_of_id IS NOT NULL");
        b.HasIndex(x => new { x.PractitionerId, x.AccruedOn });
        b.HasIndex(x => new { x.FacilityId, x.AccruedOn });
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => x.PayoutId);
    }
}

internal sealed class DoctorPayoutConfiguration : IEntityTypeConfiguration<DoctorPayout>
{
    public void Configure(EntityTypeBuilder<DoctorPayout> b)
    {
        b.ToTable("doctor_payouts", BillingSchema.Name);
        b.Property(x => x.PayoutNumber).HasMaxLength(24);
        b.Property(x => x.PaidReference).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.TdsPercent).HasPrecision(5, 2);
        b.Property(x => x.TdsRemark).HasMaxLength(1);
        b.Property(x => x.TdsPanCipher).HasMaxLength(256).IsProtectedValue();
        b.HasMany(x => x.Deductions).WithOne().HasForeignKey(x => x.PayoutId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.PayoutNumber).IsUnique();
        b.HasIndex(x => new { x.PractitionerId, x.PeriodFrom });
        b.HasIndex(x => new { x.FacilityId, x.PaidAt });
    }
}

internal sealed class DoctorPayoutDeductionConfiguration : IEntityTypeConfiguration<DoctorPayoutDeduction>
{
    public void Configure(EntityTypeBuilder<DoctorPayoutDeduction> b)
    {
        b.ToTable("doctor_payout_deductions", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(200);
        b.HasIndex(x => x.PayoutId);
    }
}

internal sealed class DoctorTaxProfileConfiguration : IEntityTypeConfiguration<DoctorTaxProfile>
{
    public void Configure(EntityTypeBuilder<DoctorTaxProfile> b)
    {
        b.ToTable("doctor_tax_profiles", BillingSchema.Name);
        b.Property(x => x.PanCipher).HasMaxLength(256).IsProtectedValue();
        b.Property(x => x.PanMasked).HasMaxLength(10);
        b.Property(x => x.NameAsPerPan).HasMaxLength(75);
        b.Property(x => x.CertificateNumber).HasMaxLength(20);
        b.Property(x => x.CertificateRate).HasPrecision(5, 2);
        b.HasIndex(x => x.PractitionerId).IsUnique().HasFilter("is_deleted = false");
    }
}
