using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class TallyExportConfiguration : IEntityTypeConfiguration<TallyExport>
{
    public void Configure(EntityTypeBuilder<TallyExport> b)
    {
        b.ToTable("tally_exports", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Message).HasMaxLength(2000);
        b.Property(x => x.FileFingerprint).HasMaxLength(64);
        b.Property(x => x.ImportedByName).HasMaxLength(120);
        b.Property(x => x.TallyCheckMessage).HasMaxLength(TallyTracking.MaxText);
        b.HasIndex(x => new { x.FacilityId, x.Date }).IsUnique();
        b.HasMany<TallyVoucherRecord>().WithOne().HasForeignKey(x => x.ExportId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TallyVoucherRecordConfiguration : IEntityTypeConfiguration<TallyVoucherRecord>
{
    public void Configure(EntityTypeBuilder<TallyVoucherRecord> b)
    {
        b.ToTable("tally_vouchers", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(200);
        b.Property(x => x.RemoteId).HasMaxLength(210);
        b.Property(x => x.VoucherType).HasMaxLength(32);
        b.Property(x => x.Number).HasMaxLength(100);
        b.Property(x => x.Hash).HasMaxLength(64);
        b.Property(x => x.SentHash).HasMaxLength(64);
        b.Property(x => x.AnswerMessage).HasMaxLength(TallyTracking.MaxText);
        b.Property(x => x.TallyNumber).HasMaxLength(100);
        b.Property(x => x.TallyDetail).HasMaxLength(TallyTracking.MaxText);
        b.Ignore(x => x.IsConfirmed);
        b.Ignore(x => x.IsCurrentInTally);
        b.Ignore(x => x.Status);

        // One row per voucher of a day book; the check against Tally looks a reference up across facilities by date.
        b.HasIndex(x => new { x.ExportId, x.Key }).IsUnique();
        b.HasIndex(x => new { x.Date, x.Key });
        b.HasIndex(x => new { x.FacilityId, x.Date });
    }
}

internal sealed class EInvoiceConfiguration : IEntityTypeConfiguration<EInvoice>
{
    public void Configure(EntityTypeBuilder<EInvoice> b)
    {
        b.ToTable("einvoices", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Source).HasMaxLength(16);
        b.Property(x => x.DocumentNumber).HasMaxLength(24);
        b.Property(x => x.BuyerGstin).HasMaxLength(15);
        b.Property(x => x.InvoiceValue).HasPrecision(18, 2);
        b.Property(x => x.Irn).HasMaxLength(64);
        b.Property(x => x.AckNo).HasMaxLength(20);
        b.Property(x => x.SignedQrCode).HasMaxLength(8000);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.Property(x => x.Status).HasMaxLength(16);
        b.Property(x => x.CancelReason).HasMaxLength(24);
        b.Property(x => x.CancelRemark).HasMaxLength(100);
        b.HasIndex(x => x.Irn).IsUnique();

        // One live IRN per invoice; cancelled ones stay as history.
        b.HasIndex(x => new { x.Source, x.SourceId }).IsUnique().HasFilter("status = 'Registered' AND is_deleted = false");
        b.HasIndex(x => new { x.FacilityId, x.DocumentDate });
    }
}
