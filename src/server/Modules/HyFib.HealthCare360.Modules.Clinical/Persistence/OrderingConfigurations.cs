using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class ClinicalTemplateConfiguration : IEntityTypeConfiguration<ClinicalTemplate>
{
    public void Configure(EntityTypeBuilder<ClinicalTemplate> b)
    {
        b.ToTable("clinical_templates", ClinicalSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.SpecialtyCode).HasMaxLength(16);
        b.Property(x => x.ChiefComplaint).HasMaxLength(1000);
        b.Property(x => x.HistoryOfPresentIllness).HasMaxLength(4000);
        b.Property(x => x.Examination).HasMaxLength(4000);
        b.Property(x => x.Assessment).HasMaxLength(4000);
        b.Property(x => x.Plan).HasMaxLength(4000);
        b.Property(x => x.FollowUpAdvice).HasMaxLength(1000);
        b.HasMany(x => x.Medications).WithOne().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.SpecialtyCode, x.IsActive });
        b.HasIndex(x => x.OwnerPractitionerId);
    }
}

internal sealed class TemplateMedicationConfiguration : IEntityTypeConfiguration<TemplateMedication>
{
    public void Configure(EntityTypeBuilder<TemplateMedication> b)
    {
        b.ToTable("template_medications", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.DrugName).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(300);
        b.Property(x => x.Strength).HasMaxLength(64);
        b.Property(x => x.DosageForm).HasMaxLength(64);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.Dose).HasMaxLength(64);
        b.Property(x => x.CustomFrequency).HasMaxLength(64);
        b.Property(x => x.Instructions).HasMaxLength(300);
    }
}

internal sealed class OrderSetConfiguration : IEntityTypeConfiguration<OrderSet>
{
    public void Configure(EntityTypeBuilder<OrderSet> b)
    {
        b.ToTable("order_sets", ClinicalSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.SpecialtyCode).HasMaxLength(16);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class PractitionerFavouriteConfiguration : IEntityTypeConfiguration<PractitionerFavourite>
{
    public void Configure(EntityTypeBuilder<PractitionerFavourite> b)
    {
        b.ToTable("practitioner_favourites", ClinicalSchema.Name);
        b.Property(x => x.Label).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(300);
        b.Property(x => x.Strength).HasMaxLength(64);
        b.Property(x => x.DosageForm).HasMaxLength(64);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.Dose).HasMaxLength(64);
        b.Property(x => x.CustomFrequency).HasMaxLength(64);
        b.Property(x => x.Instructions).HasMaxLength(300);
        b.HasIndex(x => new { x.PractitionerId, x.Kind });
    }
}

internal sealed class ClinicalOrderConfiguration : IEntityTypeConfiguration<ClinicalOrder>
{
    public void Configure(EntityTypeBuilder<ClinicalOrder> b)
    {
        b.ToTable("clinical_orders", ClinicalSchema.Name);
        b.Property(x => x.OrderNumber).HasMaxLength(24);
        b.Property(x => x.ClinicalNotes).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.OrderNumber).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.OrderedAt });
        b.HasIndex(x => new { x.Status, x.OrderedAt });
        b.HasIndex(x => x.EncounterId);
        b.HasIndex(x => x.VisitId);
        b.HasIndex(x => x.AdmissionId);
        b.HasIndex(x => x.PackageBookingId);
    }
}

internal sealed class ClinicalOrderItemConfiguration : IEntityTypeConfiguration<ClinicalOrderItem>
{
    public void Configure(EntityTypeBuilder<ClinicalOrderItem> b)
    {
        // Last line of defence behind OrderQuantity: a line always requests at least one unit or session.
        b.ToTable("clinical_order_items", ClinicalSchema.Name, t => t.HasCheckConstraint("ck_clinical_order_items_quantity", "quantity >= 1"));
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.ServiceName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ResultReference).HasMaxLength(64);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsOpen);
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => new { x.ServiceItemId, x.Status });
    }
}
