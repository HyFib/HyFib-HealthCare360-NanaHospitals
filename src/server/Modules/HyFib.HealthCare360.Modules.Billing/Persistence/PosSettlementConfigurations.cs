using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class PosSettlementImportConfiguration : IEntityTypeConfiguration<PosSettlementImport>
{
    public void Configure(EntityTypeBuilder<PosSettlementImport> b)
    {
        b.ToTable("pos_settlement_imports", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Acquirer).HasMaxLength(64);
        b.Property(x => x.FileName).HasMaxLength(200);
        b.Property(x => x.FileSha256).HasMaxLength(64);
        b.Property(x => x.ImportedByName).HasMaxLength(200);

        // The same report again is refused while it would bring nothing, so it is not unique: one imported while a report
        // withdrawn since had brought its transactions first is imported again for them. Two people importing the same
        // report at once are kept apart by the transactions' own unique index.
        b.HasIndex(x => new { x.FacilityId, x.FileSha256 });
        b.Property(x => x.WithdrawnByName).HasMaxLength(200);
        b.Property(x => x.WithdrawReason).HasMaxLength(500);
        b.HasIndex(x => new { x.FacilityId, x.ImportedAt });
    }
}

internal sealed class PosSettlementLineConfiguration : IEntityTypeConfiguration<PosSettlementLine>
{
    public void Configure(EntityTypeBuilder<PosSettlementLine> b)
    {
        b.ToTable("pos_settlement_lines", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.LineKey).HasMaxLength(160);
        b.Property(x => x.Acquirer).HasMaxLength(64);
        b.Property(x => x.TerminalId).HasMaxLength(32);
        b.Property(x => x.Rrn).HasMaxLength(32);
        b.Property(x => x.ApprovalCode).HasMaxLength(16);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.Utr).HasMaxLength(40);
        b.Property(x => x.ReviewedByName).HasMaxLength(200);
        b.Property(x => x.ReviewNote).HasMaxLength(500);
        b.Property(x => x.Counter).HasMaxLength(16);
        b.Property(x => x.CounterDocument).HasMaxLength(40);
        b.Ignore(x => x.Gross);
        b.Ignore(x => x.NeedsAttention);
        b.Ignore(x => x.IsOpen);

        // Reports overlap: a transaction another report already brought is not stored twice, unless that report was
        // withdrawn as imported in error.
        b.HasIndex(x => new { x.FacilityId, x.LineKey }).IsUnique().HasFilter("withdrawn_on IS NULL");

        // A receipt's payment is matched by one settled transaction at most, whoever matches at the same time.
        b.HasIndex(x => x.ReceiptPaymentId).IsUnique().HasFilter("receipt_payment_id IS NOT NULL");
        b.HasIndex(x => x.RefundId).IsUnique().HasFilter("refund_id IS NOT NULL");

        // So is a pharmacy or canteen payment.
        b.HasIndex(x => x.CounterPaymentId).IsUnique().HasFilter("counter_payment_id IS NOT NULL");
        b.HasIndex(x => new { x.FacilityId, x.SettledOn });

        // The day book reads a day's payouts and the reversals of reports withdrawn that day (few).
        b.HasIndex(x => new { x.FacilityId, x.WithdrawnOn }).HasFilter("withdrawn_on IS NOT NULL");
        b.HasIndex(x => new { x.FacilityId, x.Match, x.ReviewedAt });
        b.HasIndex(x => x.ImportId);
    }
}
