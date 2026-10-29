using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Organization;

internal sealed class FacilityConfiguration : IEntityTypeConfiguration<Facility>
{
    public void Configure(EntityTypeBuilder<Facility> b)
    {
        b.ToTable("facilities", "org");
        b.Property(x => x.Code).HasMaxLength(6);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.LegalName).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Website).HasMaxLength(256);
        b.Property(x => x.Gstin).HasMaxLength(15);
        b.Property(x => x.Pan).HasMaxLength(10);
        b.Property(x => x.RegistrationNumber).HasMaxLength(64);
        b.Property(x => x.NabhNumber).HasMaxLength(64);
        b.Property(x => x.HfrId).HasMaxLength(64);
        b.Property(x => x.TimeZone).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.LetterheadNote).HasMaxLength(300);
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
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departments", "org");
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.Location).HasMaxLength(128);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.TokenPrefix).HasMaxLength(4);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
    }
}

internal sealed class SpecialtyConfiguration : IEntityTypeConfiguration<Specialty>
{
    public void Configure(EntityTypeBuilder<Specialty> b)
    {
        b.ToTable("specialties", "org");
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class PractitionerConfiguration : IEntityTypeConfiguration<Practitioner>
{
    public void Configure(EntityTypeBuilder<Practitioner> b)
    {
        b.ToTable("practitioners", "org");
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Title).HasMaxLength(16);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.Qualification).HasMaxLength(200);
        b.Property(x => x.RegistrationCouncil).HasMaxLength(128);
        b.Property(x => x.RegistrationNumber).HasMaxLength(64);
        b.Property(x => x.HprId).HasMaxLength(17);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Bio).HasMaxLength(2000);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.DepartmentId);
        b.HasIndex(x => x.SpecialtyId);
        b.HasIndex(x => x.DisplayName).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class ServiceItemConfiguration : IEntityTypeConfiguration<ServiceItem>
{
    public void Configure(EntityTypeBuilder<ServiceItem> b)
    {
        b.ToTable("service_items", "org");
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ShortName).HasMaxLength(64);
        b.Property(x => x.LocalName).HasMaxLength(200);
        b.Property(x => x.SacCode).HasMaxLength(16);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.TaxRate).HasPrecision(5, 2);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.Category, x.IsActive });
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}
