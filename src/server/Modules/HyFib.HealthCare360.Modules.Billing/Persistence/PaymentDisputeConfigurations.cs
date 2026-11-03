using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class PaymentDisputeConfiguration : IEntityTypeConfiguration<PaymentDispute>
{
    public void Configure(EntityTypeBuilder<PaymentDispute> b)
    {
        b.ToTable("payment_disputes", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.ReceiptNumber).HasMaxLength(24);
        b.Property(x => x.PaymentReference).HasMaxLength(64);
        b.Property(x => x.ReasonCode).HasMaxLength(32);
        b.Property(x => x.Reason).HasMaxLength(PaymentDispute.NoteMaxLength);
        b.Property(x => x.OpenedByName).HasMaxLength(200);
        b.Property(x => x.EvidenceNote).HasMaxLength(PaymentDispute.NoteMaxLength);
        b.Property(x => x.EvidenceByName).HasMaxLength(200);
        b.Property(x => x.LossAskedByName).HasMaxLength(200);
        b.Property(x => x.LossSubjectType).HasMaxLength(64);
        b.Property(x => x.ClosedByName).HasMaxLength(200);
        b.Property(x => x.Outcome).HasMaxLength(PaymentDispute.NoteMaxLength);
        b.Property(x => x.ReversalNumber).HasMaxLength(24);
        b.Ignore(x => x.IsClosed);
        b.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReceiptPayment>().WithMany().HasForeignKey(x => x.ReceiptPaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.ReceiptPaymentId);
        b.HasIndex(x => new { x.FacilityId, x.Status });

        // One case per debit that carried the chargeback, whoever opens it at the same time.
        b.HasIndex(x => x.GatewaySettlementLineId).IsUnique().HasFilter("gateway_settlement_line_id IS NOT NULL");
        b.HasIndex(x => x.PosSettlementLineId).IsUnique().HasFilter("pos_settlement_line_id IS NOT NULL");
        b.HasIndex(x => x.ReturnLineId).IsUnique().HasFilter("return_line_id IS NOT NULL");
    }
}

internal sealed class PaymentDisputeReminderConfiguration : IEntityTypeConfiguration<PaymentDisputeReminder>
{
    public void Configure(EntityTypeBuilder<PaymentDisputeReminder> b)
    {
        b.ToTable("payment_dispute_reminders", BillingSchema.Name);
        b.HasKey(x => x.DisputeId);
        b.HasOne<PaymentDispute>().WithOne().HasForeignKey<PaymentDisputeReminder>(x => x.DisputeId).OnDelete(DeleteBehavior.Cascade);

        // The job looks for cases not told of yet.
        b.HasIndex(x => x.AnnouncedAt).HasFilter("announced_at IS NULL");
    }
}

