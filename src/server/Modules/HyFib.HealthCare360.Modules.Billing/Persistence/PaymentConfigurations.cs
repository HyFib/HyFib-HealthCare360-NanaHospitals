using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> b)
    {
        b.ToTable("receipts", BillingSchema.Name);
        b.Property(x => x.ReceiptNumber).HasMaxLength(24);
        b.Property(x => x.Counter).HasMaxLength(64);
        b.Property(x => x.ReceivedByName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.ModeSummary);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ReceiptNumber).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => x.IpAccountId);
        b.HasIndex(x => x.AdvanceId);
        b.HasIndex(x => x.ShiftId);
        b.HasIndex(x => new { x.FacilityId, x.ReceivedAt });
        b.HasIndex(x => new { x.PatientId, x.ReceivedAt });
    }
}

internal sealed class PatientAdvanceConfiguration : IEntityTypeConfiguration<PatientAdvance>
{
    public void Configure(EntityTypeBuilder<PatientAdvance> b)
    {
        b.ToTable("patient_advances", BillingSchema.Name);
        b.Property(x => x.ReceiptNumber).HasMaxLength(24);
        b.Property(x => x.AppointmentNumber).HasMaxLength(32);
        b.Property(x => x.Provider).HasMaxLength(64);
        b.Property(x => x.GatewayOrderId).HasMaxLength(128);
        b.Property(x => x.GatewayPaymentId).HasMaxLength(128);
        b.Ignore(x => x.Balance);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);

        // One advance per online payment, however many times the gateway event is replayed.
        b.HasIndex(x => x.GatewayPaymentId).IsUnique();
        b.HasIndex(x => x.ReceiptId).IsUnique();
        b.HasIndex(x => x.AppointmentId);
        b.HasIndex(x => new { x.PatientId, x.ReceivedAt });
    }
}

internal sealed class ReceiptPaymentConfiguration : IEntityTypeConfiguration<ReceiptPayment>
{
    public void Configure(EntityTypeBuilder<ReceiptPayment> b)
    {
        b.ToTable("receipt_payments", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.CardNetwork).HasMaxLength(32);
        b.Property(x => x.ChequeNumber).HasMaxLength(12);
        b.Property(x => x.ChequeBank).HasMaxLength(100);
        b.Property(x => x.ExternalId).HasMaxLength(128);

        // A POS transaction or gateway payment can be recorded only once, whatever the number of retries/webhooks.
        b.HasIndex(x => new { x.Mode, x.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        b.HasIndex(x => x.ReceiptId);

        // The acquirer's settlement report is matched by the RRN or UTR the terminal returned or the cashier typed.
        b.HasIndex(x => x.Reference).HasFilter("reference IS NOT NULL");
    }
}

internal sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("refunds", BillingSchema.Name);
        b.Property(x => x.RefundNumber).HasMaxLength(24);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.DecisionRemarks).HasMaxLength(300);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.Counter).HasMaxLength(64);
        b.Property(x => x.GatewayRefundId).HasMaxLength(128);
        b.Property(x => x.GatewayProvider).HasMaxLength(32);
        b.Property(x => x.GatewayPaymentId).HasMaxLength(128);
        b.Property(x => x.GatewayOrderId).HasMaxLength(128);
        b.Property(x => x.GatewayReference).HasMaxLength(64);
        b.Property(x => x.GatewayError).HasMaxLength(300);
        b.Ignore(x => x.IsOutstanding);
        b.Ignore(x => x.IsGatewayRefundOpen);

        // Settlement lines and refund webhooks find the refund by the gateway's id.
        b.HasIndex(x => x.GatewayRefundId).IsUnique().HasFilter("gateway_refund_id IS NOT NULL");
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RefundNumber).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => x.IpAccountId);
        b.HasIndex(x => x.AdvanceId);
        b.HasIndex(x => x.ShiftId);
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => new { x.FacilityId, x.PaidAt });
    }
}

internal sealed class ReceiptReversalConfiguration : IEntityTypeConfiguration<ReceiptReversal>
{
    public void Configure(EntityTypeBuilder<ReceiptReversal> b)
    {
        b.ToTable("receipt_reversals", BillingSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.ReceiptNumber).HasMaxLength(24);
        b.Property(x => x.Note).HasMaxLength(ReversalRules.NoteMaxLength);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.ReversalId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Receipt>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReceiptReversal>().WithMany().HasForeignKey(x => x.CausedById).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.ReceiptId);
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => x.IpAccountId);
        b.HasIndex(x => x.AdvanceId);
        b.HasIndex(x => x.CausedById);
        b.HasIndex(x => new { x.FacilityId, x.ReversedAt });
    }
}

internal sealed class ReceiptReversalLineConfiguration : IEntityTypeConfiguration<ReceiptReversalLine>
{
    public void Configure(EntityTypeBuilder<ReceiptReversalLine> b)
    {
        b.ToTable("receipt_reversal_lines", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.HasOne<ReceiptPayment>().WithMany().HasForeignKey(x => x.ReceiptPaymentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ReversalId);
        b.HasIndex(x => x.ReceiptPaymentId);
    }
}

internal sealed class RefundReversalConfiguration : IEntityTypeConfiguration<RefundReversal>
{
    public void Configure(EntityTypeBuilder<RefundReversal> b)
    {
        b.ToTable("refund_reversals", BillingSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.RefundNumber).HasMaxLength(24);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.GatewayRefundId).HasMaxLength(128);
        b.Property(x => x.Note).HasMaxLength(ReversalRules.NoteMaxLength);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.HasOne<Refund>().WithMany().HasForeignKey(x => x.RefundId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();

        // A refund's payout is undone at most once: the refund stays as recorded, and a new refund pays the patient.
        b.HasIndex(x => x.RefundId).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => x.IpAccountId);
        b.HasIndex(x => x.AdvanceId);
        b.HasIndex(x => new { x.FacilityId, x.ReversedAt });
    }
}

internal sealed class CreditNoteConfiguration : IEntityTypeConfiguration<CreditNote>
{
    public void Configure(EntityTypeBuilder<CreditNote> b)
    {
        b.ToTable("credit_notes", BillingSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.IssuedByName).HasMaxLength(200);
        b.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CreditNoteId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(x => x.GstAmount);
        b.Ignore(x => x.TaxableAmount);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => new { x.FacilityId, x.IssuedAt });
    }
}

internal sealed class CreditNoteLineConfiguration : IEntityTypeConfiguration<CreditNoteLine>
{
    public void Configure(EntityTypeBuilder<CreditNoteLine> b)
    {
        b.ToTable("credit_note_lines", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.SacCode).HasMaxLength(16);
        b.Property(x => x.GstRate).HasPrecision(5, 2);
        b.Ignore(x => x.GstAmount);

        // What earlier credit notes took off a bill line, so a line is never credited beyond its value.
        b.HasOne<BillLine>().WithMany().HasForeignKey(x => x.BillLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BillLineId);
        b.HasIndex(x => x.CreditNoteId);
    }
}

internal sealed class DebitNoteConfiguration : IEntityTypeConfiguration<DebitNote>
{
    public void Configure(EntityTypeBuilder<DebitNote> b)
    {
        b.ToTable("debit_notes", BillingSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Party).HasMaxLength(16);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.IssuedByName).HasMaxLength(200);
        b.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.DebitNoteId).OnDelete(DeleteBehavior.Cascade);
        b.Ignore(x => x.GstAmount);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => new { x.FacilityId, x.IssuedAt });
    }
}

internal sealed class DebitNoteLineConfiguration : IEntityTypeConfiguration<DebitNoteLine>
{
    public void Configure(EntityTypeBuilder<DebitNoteLine> b)
    {
        b.ToTable("debit_note_lines", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.SacCode).HasMaxLength(16);
        b.Property(x => x.GstRate).HasPrecision(5, 2);
        b.Ignore(x => x.GstAmount);
        b.HasOne<BillLine>().WithMany().HasForeignKey(x => x.BillLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BillLineId);
        b.HasIndex(x => x.DebitNoteId);
    }
}

internal sealed class CashierShiftConfiguration : IEntityTypeConfiguration<CashierShift>
{
    public void Configure(EntityTypeBuilder<CashierShift> b)
    {
        b.ToTable("cashier_shifts", BillingSchema.Name);
        b.Property(x => x.UserName).HasMaxLength(200);
        b.Property(x => x.Counter).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ShortageAcceptedByName).HasMaxLength(200);
        b.Ignore(x => x.Shortage);
        b.HasMany(x => x.Totals).WithOne().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Cascade);

        // One open shift per cashier: every counter receipt belongs to exactly one drawer.
        b.HasIndex(x => x.UserId, "ix_cashier_shifts_one_open_per_user").IsUnique().HasFilter("status = 'Open' AND is_deleted = false");
        b.HasIndex(x => new { x.FacilityId, x.OpenedAt });
    }
}

internal sealed class CashierShiftTotalConfiguration : IEntityTypeConfiguration<CashierShiftTotal>
{
    public void Configure(EntityTypeBuilder<CashierShiftTotal> b)
    {
        b.ToTable("cashier_shift_totals", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ShiftId);
    }
}

internal sealed class GatewayOrderConfiguration : IEntityTypeConfiguration<GatewayOrder>
{
    public void Configure(EntityTypeBuilder<GatewayOrder> b)
    {
        b.ToTable("gateway_orders", BillingSchema.Name);
        b.Property(x => x.Provider).HasMaxLength(64);
        b.Property(x => x.OrderId).HasMaxLength(128);
        b.Property(x => x.MerchantReference).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.LinkUrl).HasMaxLength(500);
        b.Property(x => x.CheckoutKey).HasMaxLength(128);
        b.Property(x => x.GatewayPaymentId).HasMaxLength(128);
        b.Property(x => x.Method).HasMaxLength(32);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Ignore(x => x.IsAwaitingPayment);
        b.HasIndex(x => new { x.Provider, x.OrderId }).IsUnique();
        b.HasIndex(x => x.MerchantReference).IsUnique();
        b.HasIndex(x => x.GatewayPaymentId).IsUnique().HasFilter("gateway_payment_id IS NOT NULL");
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

internal sealed class PosTransactionConfiguration : IEntityTypeConfiguration<PosTransaction>
{
    public void Configure(EntityTypeBuilder<PosTransaction> b)
    {
        b.ToTable("pos_transactions", BillingSchema.Name);
        b.Property(x => x.Provider).HasMaxLength(64);
        b.Property(x => x.TerminalId).HasMaxLength(64);
        b.Property(x => x.TransactionId).HasMaxLength(128);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.ApprovalCode).HasMaxLength(32);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.Rrn).HasMaxLength(32);
        b.Property(x => x.Message).HasMaxLength(300);
        b.Property(x => x.Counter).HasMaxLength(64);
        b.Ignore(x => x.IsComplete);
        b.HasIndex(x => new { x.Provider, x.TransactionId }).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => x.ApprovalCode).HasFilter("approval_code IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
