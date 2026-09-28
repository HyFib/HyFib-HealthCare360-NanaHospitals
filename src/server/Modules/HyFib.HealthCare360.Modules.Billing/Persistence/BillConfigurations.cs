using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class ChargeConfiguration : IEntityTypeConfiguration<Charge>
{
    public void Configure(EntityTypeBuilder<Charge> b)
    {
        b.ToTable("charges", BillingSchema.Name);
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.SourceModule).HasMaxLength(64);
        b.Property(x => x.SourceReference).HasMaxLength(128);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.DiscountPercent).HasPrecision(5, 2);
        b.Property(x => x.TaxRate).HasPrecision(5, 2);
        b.Property(x => x.BedMultiplier).HasPrecision(9, 4);
        b.Ignore(x => x.IsAvailableForBilling);
        b.Ignore(x => x.IsCredit);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.ServiceItemId).OnDelete(DeleteBehavior.Restrict);

        // Charge capture is idempotent: one charge per source line, ever (WiFi retries can never double-charge).
        b.HasIndex(x => new { x.SourceModule, x.SourceReference }).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => new { x.VisitId, x.Status });
        b.HasIndex(x => new { x.AdmissionId, x.Status });
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => new { x.PractitionerId, x.ServiceDate });
    }
}

internal sealed class BillConfiguration : IEntityTypeConfiguration<Bill>
{
    public void Configure(EntityTypeBuilder<Bill> b)
    {
        b.ToTable("bills", BillingSchema.Name);
        b.Property(x => x.BillNumber).HasMaxLength(24);
        b.Property(x => x.DiscountReason).HasMaxLength(300);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CoPayPercent).HasPrecision(5, 2);
        b.Property(x => x.BillDiscountPercent).HasPrecision(5, 2);
        b.Ignore(x => x.PayerOutstanding);
        b.Ignore(x => x.IsOpenForPayment);
        b.Ignore(x => x.HasPayer);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.BillNumber).IsUnique().HasFilter("bill_number IS NOT NULL");
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => new { x.VisitId, x.Status });
        b.HasIndex(x => new { x.AdmissionId, x.Status });
        b.HasIndex(x => new { x.FacilityId, x.FinalizedAt });
        b.HasIndex(x => new { x.FacilityId, x.CancelledAt });
        b.HasIndex(x => new { x.PayerId, x.Status });

        // At most one open draft per OP visit and one draft final bill per admission (two desks cannot race).
        b.HasIndex(x => x.VisitId, "ix_bills_one_op_draft_per_visit")
            .IsUnique()
            .HasFilter("status = 'Draft' AND type = 'OutPatient' AND visit_id IS NOT NULL AND is_deleted = false");
        b.HasIndex(x => x.AdmissionId, "ix_bills_one_draft_final_per_admission")
            .IsUnique()
            .HasFilter("status = 'Draft' AND is_final_bill = true AND is_deleted = false");
    }
}

internal sealed class BillLineConfiguration : IEntityTypeConfiguration<BillLine>
{
    public void Configure(EntityTypeBuilder<BillLine> b)
    {
        b.ToTable("bill_lines", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.SacCode).HasMaxLength(16);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.TariffDiscountPercent).HasPrecision(5, 2);
        b.Property(x => x.TaxRate).HasPrecision(5, 2);
        b.Ignore(x => x.DiscountableAmount);
        b.HasIndex(x => x.ChargeId);
        b.HasIndex(x => new { x.PractitionerId, x.ServiceDate });
    }
}

internal sealed class DiscountApprovalConfiguration : IEntityTypeConfiguration<DiscountApproval>
{
    public void Configure(EntityTypeBuilder<DiscountApproval> b)
    {
        b.ToTable("discount_approvals", BillingSchema.Name);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.LineDiscountsJson).HasColumnType("jsonb");
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.Property(x => x.BillDiscountPercent).HasPrecision(5, 2);
        b.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => x.BillId);
    }
}

internal sealed class DiscountLimitConfiguration : IEntityTypeConfiguration<DiscountLimit>
{
    public void Configure(EntityTypeBuilder<DiscountLimit> b)
    {
        b.ToTable("discount_limits", BillingSchema.Name);
        b.Property(x => x.MaxPercent).HasPrecision(5, 2);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("is_deleted = false");
    }
}
