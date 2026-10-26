using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Persistence;

internal static class InteropSchema
{
    public const string Name = "interop";
}

internal sealed class AbdmLinkRequestConfiguration : IEntityTypeConfiguration<AbdmLinkRequest>
{
    public void Configure(EntityTypeBuilder<AbdmLinkRequest> b)
    {
        b.ToTable("abdm_link_requests", InteropSchema.Name);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.CareContextsJson).HasColumnType("jsonb");
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.Error).HasMaxLength(500);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });

        // The gateway's answers find their request by the id it was sent under.
        b.HasIndex(x => x.TokenRequestId).IsUnique().HasFilter("token_request_id IS NOT NULL");
        b.HasIndex(x => x.LinkRequestId).IsUnique().HasFilter("link_request_id IS NOT NULL");
    }
}

internal sealed class AbdmLinkTokenConfiguration : IEntityTypeConfiguration<AbdmLinkToken>
{
    public void Configure(EntityTypeBuilder<AbdmLinkToken> b)
    {
        b.ToTable("abdm_link_tokens", InteropSchema.Name);
        b.Property(x => x.HipId).HasMaxLength(64);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.TokenCipher).HasMaxLength(8000).IsProtectedValue();
        b.HasIndex(x => new { x.HipId, x.AbhaAddress }).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class AbdmLinkedCareContextConfiguration : IEntityTypeConfiguration<AbdmLinkedCareContext>
{
    public void Configure(EntityTypeBuilder<AbdmLinkedCareContext> b)
    {
        b.ToTable("abdm_care_contexts", InteropSchema.Name);
        b.Property(x => x.HipId).HasMaxLength(64);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.ReferenceNumber).HasMaxLength(100);
        b.Property(x => x.Display).HasMaxLength(200);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PatientId);

        // A record is linked to an ABHA address once; the consent manager names it by its reference.
        b.HasIndex(x => new { x.HipId, x.AbhaAddress, x.ReferenceNumber }).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class AbdmDiscoveryConfiguration : IEntityTypeConfiguration<AbdmDiscovery>
{
    public void Configure(EntityTypeBuilder<AbdmDiscovery> b)
    {
        b.ToTable("abdm_discoveries", InteropSchema.Name);
        b.Property(x => x.TransactionId).HasMaxLength(64);
        b.Property(x => x.HipId).HasMaxLength(64);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.MatchedBy).HasMaxLength(64);
        b.Property(x => x.OfferedJson).HasColumnType("jsonb");
        b.Property(x => x.SelectedJson).HasColumnType("jsonb");
        b.Property(x => x.CodeHash).HasMaxLength(128);
        b.Property(x => x.Error).HasMaxLength(500);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });

        // The gateway's link-init call names the discovery by its transaction id, and its confirm call by the link reference.
        b.HasIndex(x => x.TransactionId).IsUnique();
        b.HasIndex(x => x.LinkReference).IsUnique().HasFilter("link_reference IS NOT NULL");
    }
}

internal sealed class AbdmConsentConfiguration : IEntityTypeConfiguration<AbdmConsent>
{
    public void Configure(EntityTypeBuilder<AbdmConsent> b)
    {
        b.ToTable("abdm_consents", InteropSchema.Name);
        b.Property(x => x.ConsentId).HasMaxLength(64);
        b.Property(x => x.HipId).HasMaxLength(64);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.HiuId).HasMaxLength(64);
        b.Property(x => x.Purpose).HasMaxLength(200);
        b.Property(x => x.HiTypes).HasMaxLength(500);
        b.Property(x => x.CareContextsJson).HasColumnType("jsonb");
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ConsentId).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.NotifiedAt });
    }
}

internal sealed class AbdmTransferConfiguration : IEntityTypeConfiguration<AbdmTransfer>
{
    public void Configure(EntityTypeBuilder<AbdmTransfer> b)
    {
        b.ToTable("abdm_transfers", InteropSchema.Name);
        b.Property(x => x.TransactionId).HasMaxLength(64);
        b.Property(x => x.ConsentId).HasMaxLength(64);
        b.Property(x => x.HipId).HasMaxLength(64);
        b.Property(x => x.DataPushUrl).HasMaxLength(500);
        b.Property(x => x.KeyMaterialJson).HasColumnType("jsonb");
        b.Property(x => x.ResultsJson).HasColumnType("jsonb");
        b.Property(x => x.Error).HasMaxLength(500);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TransactionId).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.RequestedAt });
    }
}

internal sealed class AbdmProfileShareConfiguration : IEntityTypeConfiguration<AbdmProfileShare>
{
    public void Configure(EntityTypeBuilder<AbdmProfileShare> b)
    {
        b.ToTable("abdm_profile_shares", InteropSchema.Name);
        b.Property(x => x.HipId).HasMaxLength(64);
        b.Property(x => x.Context).HasMaxLength(64);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.AbhaNumberCipher).HasMaxLength(500).IsProtectedValue();
        b.Property(x => x.AbhaNumberMasked).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Gender).HasMaxLength(8);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.AddressLine).HasMaxLength(500);
        b.Property(x => x.District).HasMaxLength(100);
        b.Property(x => x.State).HasMaxLength(100);
        b.Property(x => x.Pincode).HasMaxLength(10);
        b.Property(x => x.MatchedBy).HasMaxLength(32);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // Token numbers run per hospital per day; a second share in the same hour reuses its token.
        b.HasIndex(x => new { x.HipId, x.TokenDate, x.TokenNumber }).IsUnique();
        b.HasIndex(x => new { x.HipId, x.AbhaAddress, x.Context, x.SharedAt });
        b.HasIndex(x => new { x.PatientId, x.SharedAt });
    }
}

internal sealed class AbdmAbhaVerificationConfiguration : IEntityTypeConfiguration<AbdmAbhaVerification>
{
    public void Configure(EntityTypeBuilder<AbdmAbhaVerification> b)
    {
        b.ToTable("abdm_abha_verifications", InteropSchema.Name);
        b.Property(x => x.TxnId).HasMaxLength(100);
        b.Property(x => x.SentMessage).HasMaxLength(300);
        b.Property(x => x.AbhaNumberMasked).HasMaxLength(32);
        b.Property(x => x.AbhaAddress).HasMaxLength(100);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Gender).HasMaxLength(8);
        b.Property(x => x.DifferencesJson).HasColumnType("jsonb");
        b.Property(x => x.Error).HasMaxLength(500);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });
    }
}
