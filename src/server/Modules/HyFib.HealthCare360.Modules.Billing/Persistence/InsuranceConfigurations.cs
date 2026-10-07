using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class PayerConfiguration : IEntityTypeConfiguration<Payer>
{
    public void Configure(EntityTypeBuilder<Payer> b)
    {
        b.ToTable("payers", BillingSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ContactPerson).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.Gstin).HasMaxLength(15);
        b.Property(x => x.PortalUrl).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.DefaultCoPayPercent).HasPrecision(5, 2);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.Category, x.IsActive });
    }
}

internal sealed class NonPayableItemConfiguration : IEntityTypeConfiguration<NonPayableItem>
{
    public void Configure(EntityTypeBuilder<NonPayableItem> b)
    {
        b.ToTable("non_payable_items", BillingSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => x.PayerId);
    }
}

internal sealed class PatientPolicyConfiguration : IEntityTypeConfiguration<PatientPolicy>
{
    public void Configure(EntityTypeBuilder<PatientPolicy> b)
    {
        b.ToTable("patient_policies", BillingSchema.Name);
        b.Property(x => x.PolicyNumberCipher).HasMaxLength(256);
        b.Property(x => x.PolicyNumberIndex).HasMaxLength(64);
        b.Property(x => x.PolicyNumberMasked).HasMaxLength(40);
        b.Property(x => x.MemberId).HasMaxLength(64);
        b.Property(x => x.PolicyHolderName).HasMaxLength(200);
        b.Property(x => x.RelationToHolder).HasMaxLength(64);
        b.Property(x => x.VerificationNotes).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CoPayPercent).HasPrecision(5, 2);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Payer>().WithMany().HasForeignKey(x => x.PayerId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.IsActive });
        b.HasIndex(x => x.PolicyNumberIndex);
    }
}

internal sealed class PreAuthorizationConfiguration : IEntityTypeConfiguration<PreAuthorization>
{
    public void Configure(EntityTypeBuilder<PreAuthorization> b)
    {
        b.ToTable("pre_authorizations", BillingSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.DiagnosisCodes).HasMaxLength(1000);
        b.Property(x => x.ProcedureCodes).HasMaxLength(1000);
        b.Property(x => x.ClinicalSummary).HasMaxLength(4000);
        b.Property(x => x.ApprovalReference).HasMaxLength(64);
        b.Property(x => x.QueryText).HasMaxLength(2000);
        b.Property(x => x.QueryResponse).HasMaxLength(2000);
        b.Property(x => x.RejectionReason).HasMaxLength(1000);
        b.Ignore(x => x.IsApproved);
        b.Ignore(x => x.EffectiveApprovedAmount);
        b.HasMany(x => x.Enhancements).WithOne().HasForeignKey(x => x.PreAuthorizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PatientPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.AdmissionId);
        b.HasIndex(x => x.VisitId);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

internal sealed class PreAuthEnhancementConfiguration : IEntityTypeConfiguration<PreAuthEnhancement>
{
    public void Configure(EntityTypeBuilder<PreAuthEnhancement> b)
    {
        b.ToTable("pre_auth_enhancements", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.HasIndex(x => x.PreAuthorizationId);
    }
}

internal sealed class InsuranceClaimConfiguration : IEntityTypeConfiguration<InsuranceClaim>
{
    public void Configure(EntityTypeBuilder<InsuranceClaim> b)
    {
        b.ToTable("insurance_claims", BillingSchema.Name);
        b.Property(x => x.ClaimNumber).HasMaxLength(24);
        b.Property(x => x.DiagnosisCodes).HasMaxLength(InsuranceClaim.CodesMaxLength);
        b.Property(x => x.ProcedureCodes).HasMaxLength(InsuranceClaim.CodesMaxLength);
        b.Property(x => x.PayerClaimReference).HasMaxLength(64);
        b.Property(x => x.QueryText).HasMaxLength(2000);
        b.Property(x => x.QueryResponse).HasMaxLength(2000);
        b.Property(x => x.UtrNumber).HasMaxLength(64);
        b.Property(x => x.RejectionReason).HasMaxLength(1000);
        b.Ignore(x => x.Outstanding);
        b.Ignore(x => x.IsClosed);
        b.HasMany(x => x.Settlements).WithOne().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Deductions).WithOne().HasForeignKey(x => x.ClaimId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ClaimNumber).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => new { x.PayerId, x.Status });
        b.HasIndex(x => new { x.Status, x.SubmittedAt });

        // A patient's claims, for their data summary: which insurers received their data (DPDP access requests).
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class ClaimSettlementConfiguration : IEntityTypeConfiguration<ClaimSettlement>
{
    public void Configure(EntityTypeBuilder<ClaimSettlement> b)
    {
        b.ToTable("claim_settlements", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.UtrNumber).HasMaxLength(64);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.HasIndex(x => x.ClaimId);
        b.HasIndex(x => x.RecordedAt);
    }
}

internal sealed class ClaimDeductionConfiguration : IEntityTypeConfiguration<ClaimDeduction>
{
    public void Configure(EntityTypeBuilder<ClaimDeduction> b)
    {
        b.ToTable("claim_deductions", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasIndex(x => x.ClaimId);
    }
}
