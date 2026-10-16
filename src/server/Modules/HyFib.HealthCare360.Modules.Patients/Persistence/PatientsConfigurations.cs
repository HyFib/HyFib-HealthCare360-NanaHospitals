using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Patients.Persistence;

internal static class PatientsSchema
{
    public const string Name = "patients";
}

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> b)
    {
        b.ToTable("patients", PatientsSchema.Name);
        b.Property(x => x.Uhid).HasMaxLength(20);
        b.Property(x => x.Title).HasMaxLength(16);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.MiddleName).HasMaxLength(100);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.FullName).HasMaxLength(302);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.AlternatePhone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.GuardianName).HasMaxLength(200);
        b.Property(x => x.EmergencyContactName).HasMaxLength(200);
        b.Property(x => x.EmergencyContactPhone).HasMaxLength(20);
        b.Property(x => x.EmergencyContactRelation).HasMaxLength(64);
        b.Property(x => x.Occupation).HasMaxLength(100);
        b.Property(x => x.Religion).HasMaxLength(64);
        b.Property(x => x.Nationality).HasMaxLength(64);
        b.Property(x => x.PreferredLanguage).HasMaxLength(32);
        b.Property(x => x.AbhaNumberCipher).HasMaxLength(256).IsProtectedValue();
        b.Property(x => x.AbhaNumberIndex).HasMaxLength(64).IsBlindIndexOf(nameof(Patient.AbhaNumberCipher));
        b.Property(x => x.AbhaNumberMasked).HasMaxLength(20);
        b.Property(x => x.AbhaAddress).HasMaxLength(128);
        b.Property(x => x.AadhaarLast4).HasMaxLength(4);
        // On the original key for good (FixedBlindIndex): only the last four digits are kept, so it cannot be computed again.
        b.Property(x => x.AadhaarIndex).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.ComplexProperty(x => x.Address, a =>
        {
            a.Property(p => p.Line1).HasMaxLength(200);
            a.Property(p => p.Line2).HasMaxLength(200);
            a.Property(p => p.City).HasMaxLength(100);
            a.Property(p => p.District).HasMaxLength(100);
            a.Property(p => p.State).HasMaxLength(100);
            a.Property(p => p.PinCode).HasMaxLength(10);
            a.Property(p => p.Country).HasMaxLength(100);
        });

        b.HasIndex(x => x.Uhid).IsUnique();
        b.HasIndex(x => x.Phone);
        b.HasIndex(x => x.AbhaNumberIndex);
        b.HasIndex(x => x.AadhaarIndex);
        b.HasIndex(x => x.RegisteredAt);
        b.HasIndex(x => new { x.FirstName, x.DateOfBirth });
        // Fuzzy name search (PERF-02): trigram GIN index.
        b.HasIndex(x => x.FullName).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class VisitConfiguration : IEntityTypeConfiguration<Visit>
{
    public void Configure(EntityTypeBuilder<Visit> b)
    {
        b.ToTable("visits", PatientsSchema.Name);
        b.Property(x => x.VisitNumber).HasMaxLength(24);
        b.Property(x => x.ReferredBy).HasMaxLength(200);
        b.Property(x => x.ChiefComplaint).HasMaxLength(500);
        b.Property(x => x.MlcNumber).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.VisitNumber).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.VisitedAt });
        b.HasIndex(x => new { x.FacilityId, x.VisitedAt });
        b.HasIndex(x => new { x.PractitionerId, x.VisitedAt });
        b.HasIndex(x => x.AppointmentId);
    }
}

internal sealed class QueueTokenConfiguration : IEntityTypeConfiguration<QueueToken>
{
    public void Configure(EntityTypeBuilder<QueueToken> b)
    {
        b.ToTable("queue_tokens", PatientsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(16);
        b.Property(x => x.Room).HasMaxLength(32);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.FacilityId, x.DepartmentId, x.TokenDate, x.Sequence }).IsUnique();
        b.HasIndex(x => new { x.TokenDate, x.Status });
        b.HasIndex(x => x.VisitId);
    }
}

internal sealed class EnquiryConfiguration : IEntityTypeConfiguration<Enquiry>
{
    public void Configure(EntityTypeBuilder<Enquiry> b)
    {
        b.ToTable("enquiries", PatientsSchema.Name);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Details).HasMaxLength(2000);
        b.Property(x => x.Response).HasMaxLength(2000);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

internal sealed class VisitorPassConfiguration : IEntityTypeConfiguration<VisitorPass>
{
    public void Configure(EntityTypeBuilder<VisitorPass> b)
    {
        b.ToTable("visitor_passes", PatientsSchema.Name);
        b.Property(x => x.PassNumber).HasMaxLength(24);
        b.Property(x => x.VisitorName).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Relation).HasMaxLength(64);
        b.Property(x => x.IdProofType).HasMaxLength(64);
        b.Property(x => x.IdProofLast4).HasMaxLength(4);
        b.Property(x => x.Purpose).HasMaxLength(200);
        b.Property(x => x.Location).HasMaxLength(128);
        b.HasIndex(x => x.PassNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.ValidUntil });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class PortalFamilyLinkConfiguration : IEntityTypeConfiguration<PortalFamilyLink>
{
    public void Configure(EntityTypeBuilder<PortalFamilyLink> b)
    {
        b.ToTable("portal_family_links", PatientsSchema.Name);
        b.Property(x => x.AccountPhone).HasMaxLength(20);
        b.Property(x => x.ConsentGivenBy).HasMaxLength(200);
        b.Property(x => x.CodeSentTo).HasMaxLength(20);
        b.Property(x => x.RevokedReason).HasMaxLength(300);
        b.Ignore(x => x.IsActive);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // One link in force per account and patient; ended links stay as history.
        b.HasIndex(x => new { x.AccountPhone, x.PatientId }).IsUnique().HasFilter("revoked_at IS NULL");
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class BreakGlassAccessConfiguration : IEntityTypeConfiguration<BreakGlassAccess>
{
    public void Configure(EntityTypeBuilder<BreakGlassAccess> b)
    {
        b.ToTable("break_glass_grants", PatientsSchema.Name);
        b.HasKey(x => x.Id);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // Every opening of a restricted record looks up the user's grant for it.
        b.HasIndex(x => new { x.PatientId, x.UserId, x.ExpiresAt });
    }
}

internal sealed class PortalFamilyCodeConfiguration : IEntityTypeConfiguration<PortalFamilyCode>
{
    public void Configure(EntityTypeBuilder<PortalFamilyCode> b)
    {
        b.ToTable("portal_family_codes", PatientsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountPhone).HasMaxLength(20);
        b.Property(x => x.TargetPhone).HasMaxLength(20);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.HasIndex(x => new { x.AccountPhone, x.CreatedAt });
        b.HasIndex(x => new { x.TargetPhone, x.CreatedAt });
    }
}

internal sealed class PatientConsentConfiguration : IEntityTypeConfiguration<PatientConsent>
{
    public void Configure(EntityTypeBuilder<PatientConsent> b)
    {
        b.ToTable("patient_consents", PatientsSchema.Name);
        b.Property(x => x.ConsentVersion).HasMaxLength(16);
        b.Property(x => x.WitnessName).HasMaxLength(200);
        b.Property(x => x.SignedByRelation).HasMaxLength(64);
        b.Property(x => x.SignedByName).HasMaxLength(200);
        b.Property(x => x.RevokedReason).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Ignore(x => x.IsActive);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.Type });
    }
}

internal sealed class PatientDocumentConfiguration : IEntityTypeConfiguration<PatientDocument>
{
    public void Configure(EntityTypeBuilder<PatientDocument> b)
    {
        b.ToTable("patient_documents", PatientsSchema.Name);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Source).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PatientId);
    }
}
