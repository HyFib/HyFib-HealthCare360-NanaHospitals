using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Identity;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", "identity");
        b.Property(x => x.UserName).HasMaxLength(64);
        b.Property(x => x.NormalizedUserName).HasMaxLength(64);
        b.Property(x => x.DisplayName).HasMaxLength(128);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.PasswordHash).HasMaxLength(512);
        b.Property(x => x.MfaSecret).HasMaxLength(512).IsProtectedValue();
        b.Property(x => x.PinHash).HasMaxLength(512);
        b.HasIndex(x => x.NormalizedUserName).IsUnique();
        b.HasIndex(x => new { x.Type, x.Phone });
        b.HasIndex(x => x.PractitionerId);
        b.HasIndex(x => x.PatientId);
        b.HasMany(x => x.Roles).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Facilities).WithOne().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles", "identity");
        b.Property(x => x.Name).HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(256);
        b.HasIndex(x => x.Name).IsUnique();
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions", "identity");
        b.HasKey(x => new { x.RoleId, x.Permission });
        b.Property(x => x.Permission).HasMaxLength(128);
    }
}

internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("user_roles", "identity");
        b.HasKey(x => new { x.UserId, x.RoleId });
        b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class UserFacilityConfiguration : IEntityTypeConfiguration<UserFacility>
{
    public void Configure(EntityTypeBuilder<UserFacility> b)
    {
        b.ToTable("user_facilities", "identity");
        b.HasKey(x => new { x.UserId, x.FacilityId });
    }
}

internal sealed class SeededGrantConfiguration : IEntityTypeConfiguration<SeededGrant>
{
    public void Configure(EntityTypeBuilder<SeededGrant> b)
    {
        b.ToTable("seeded_grants", "identity");
        b.HasKey(x => new { x.RoleName, x.Permission });
        b.Property(x => x.RoleName).HasMaxLength(64);
        b.Property(x => x.Permission).HasMaxLength(128);
    }
}

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> b)
    {
        b.ToTable("user_sessions", "identity");
        b.HasKey(x => x.Id);
        b.Property(x => x.ClientType).HasMaxLength(16);
        b.Property(x => x.RevokedReason).HasMaxLength(128);
        b.Property(x => x.IpAddress).HasMaxLength(64);
        b.Property(x => x.UserAgent).HasMaxLength(512);
        b.HasIndex(x => new { x.UserId, x.RevokedAt });
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.ToTable("refresh_tokens", "identity");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.SessionId);
        b.HasIndex(x => x.ExpiresAt);
    }
}

internal sealed class AuthChallengeConfiguration : IEntityTypeConfiguration<AuthChallenge>
{
    public void Configure(EntityTypeBuilder<AuthChallenge> b)
    {
        b.ToTable("auth_challenges", "identity");
        b.HasKey(x => x.Id);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.ClientType).HasMaxLength(16);
        b.HasIndex(x => new { x.Phone, x.CreatedAt });
        b.HasIndex(x => x.ExpiresAt);
        b.HasIndex(x => x.SessionId);
    }
}

internal sealed class WebAuthnCredentialConfiguration : IEntityTypeConfiguration<WebAuthnCredential>
{
    public void Configure(EntityTypeBuilder<WebAuthnCredential> b)
    {
        b.ToTable("webauthn_credentials", "identity");
        b.HasKey(x => x.Id);

        // WebAuthn allows ids of up to 1023 bytes; Windows Hello's are far shorter.
        b.Property(x => x.CredentialId).HasMaxLength(1023);
        b.Property(x => x.PublicKey).HasMaxLength(2048);
        b.Property(x => x.RelyingPartyId).HasMaxLength(253);
        b.Property(x => x.Name).HasMaxLength(64);
        b.Property(x => x.Transports).HasMaxLength(128);
        b.HasIndex(x => x.CredentialId).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices", "identity");
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.MachineName).HasMaxLength(128);
        b.Property(x => x.OsVersion).HasMaxLength(128);
        b.Property(x => x.AppVersion).HasMaxLength(32);
        b.Property(x => x.SecretHash).HasMaxLength(64);
        b.Property(x => x.LastIpAddress).HasMaxLength(64);
        b.Property(x => x.LastWifiSsid).HasMaxLength(64);
        b.Property(x => x.Location).HasMaxLength(128);
        b.Property(x => x.TrustedCaFingerprints).HasMaxLength(DeviceCaFingerprints.MaxStoredLength);
        b.HasIndex(x => x.FacilityId);
    }
}

internal sealed class DeviceEnrollmentCodeConfiguration : IEntityTypeConfiguration<DeviceEnrollmentCode>
{
    public void Configure(EntityTypeBuilder<DeviceEnrollmentCode> b)
    {
        b.ToTable("device_enrollment_codes", "identity");
        b.HasKey(x => x.Id);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.Note).HasMaxLength(256);
        b.HasIndex(x => x.CodeHash).IsUnique();
    }
}
