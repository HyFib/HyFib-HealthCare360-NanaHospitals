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
