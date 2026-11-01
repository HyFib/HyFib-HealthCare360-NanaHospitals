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
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.BillId);
        b.HasIndex(x => new { x.FacilityId, x.IssuedAt });
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
