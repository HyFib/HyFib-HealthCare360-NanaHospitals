using HyFib.HealthCare360.Modules.Interop.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Persistence;

internal sealed class SmartClientConfiguration : IEntityTypeConfiguration<SmartClient>
{
    public void Configure(EntityTypeBuilder<SmartClient> b)
    {
        b.ToTable("smart_clients", InteropSchema.Name);
        b.Property(x => x.ClientId).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
        b.Property(x => x.RedirectUrisJson).HasColumnType("jsonb");
        b.Property(x => x.JwksJson).HasColumnType("jsonb");
        b.Property(x => x.Scopes).HasMaxLength(2000);
        b.Property(x => x.SuspendedReason).HasMaxLength(500);
        b.HasIndex(x => x.ClientId).IsUnique();
        b.HasIndex(x => x.ServiceUserId);
    }
}

internal sealed class SmartAuthorizationCodeConfiguration : IEntityTypeConfiguration<SmartAuthorizationCode>
{
    public void Configure(EntityTypeBuilder<SmartAuthorizationCode> b)
    {
        b.ToTable("smart_authorization_codes", InteropSchema.Name);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.RedirectUri).HasMaxLength(500);
        b.Property(x => x.CodeChallenge).HasMaxLength(128);
        b.Property(x => x.Scopes).HasMaxLength(2000);
        b.HasOne<SmartClient>().WithMany().HasForeignKey(x => x.SmartClientId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.CodeHash).IsUnique();
    }
}

internal sealed class SmartAccessTokenConfiguration : IEntityTypeConfiguration<SmartAccessToken>
{
    public void Configure(EntityTypeBuilder<SmartAccessToken> b)
    {
        b.ToTable("smart_access_tokens", InteropSchema.Name);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.GrantType).HasMaxLength(32);
        b.Property(x => x.Scopes).HasMaxLength(2000);
        b.HasOne<SmartClient>().WithMany().HasForeignKey(x => x.SmartClientId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.SmartClientId, x.ExpiresAt });
    }
}

internal sealed class SmartAssertionUseConfiguration : IEntityTypeConfiguration<SmartAssertionUse>
{
    public void Configure(EntityTypeBuilder<SmartAssertionUse> b)
    {
        b.ToTable("smart_assertion_uses", InteropSchema.Name);
        b.Property(x => x.Jti).HasMaxLength(200);
        b.HasOne<SmartClient>().WithMany().HasForeignKey(x => x.SmartClientId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SmartClientId, x.Jti }).IsUnique();
    }
}
