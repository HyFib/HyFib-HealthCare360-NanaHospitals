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
