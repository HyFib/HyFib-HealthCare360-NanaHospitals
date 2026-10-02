using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class GatewaySettlementConfiguration : IEntityTypeConfiguration<GatewaySettlement>
{
    public void Configure(EntityTypeBuilder<GatewaySettlement> b)
    {
        b.ToTable("gateway_settlements", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.SettlementId).HasMaxLength(64);
        b.Property(x => x.Utr).HasMaxLength(64);
        b.Ignore(x => x.Difference);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.GatewaySettlementId).OnDelete(DeleteBehavior.Cascade);

        // Stored once however often a day is fetched, by the job or by hand.
        b.HasIndex(x => new { x.Provider, x.SettlementId }).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.SettledOn });
        b.HasIndex(x => x.SettledOn);
    }
}

internal sealed class GatewaySettlementLineConfiguration : IEntityTypeConfiguration<GatewaySettlementLine>
{
    public void Configure(EntityTypeBuilder<GatewaySettlementLine> b)
    {
        b.ToTable("gateway_settlement_lines", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.PaymentId).HasMaxLength(64);
        b.Property(x => x.OrderId).HasMaxLength(64);
        b.Property(x => x.Method).HasMaxLength(32);
        b.Ignore(x => x.Net);
        b.Ignore(x => x.Gross);
        b.HasIndex(x => x.GatewaySettlementId);

        // A receipt is "settled" when a line carries its gateway payment id; refunds are found the same way.
        b.HasIndex(x => x.EntityId);
        b.HasIndex(x => new { x.FacilityId, x.SettledAt });
    }
}

internal sealed class GatewayDiscrepancyConfiguration : IEntityTypeConfiguration<GatewayDiscrepancy>
{
    public void Configure(EntityTypeBuilder<GatewayDiscrepancy> b)
    {
        b.ToTable("gateway_discrepancies", BillingSchema.Name);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.OrderId).HasMaxLength(64);
        b.Property(x => x.Summary).HasMaxLength(300);
        b.Property(x => x.ResolvedByName).HasMaxLength(200);
        b.Property(x => x.Resolution).HasMaxLength(500);

        // One exception per kind and gateway id, so the job, webhooks and staff actions update the same row.
        b.HasIndex(x => new { x.Kind, x.Key }).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.Rank });
        b.HasIndex(x => x.GatewayOrderRecordId);
        b.HasIndex(x => x.RefundId);
    }
}

internal sealed class GatewayWebhookEventConfiguration : IEntityTypeConfiguration<GatewayWebhookEvent>
{
    public void Configure(EntityTypeBuilder<GatewayWebhookEvent> b)
    {
        b.ToTable("gateway_webhook_events", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.EventId).HasMaxLength(80);
        b.Property(x => x.EventType).HasMaxLength(64);
        b.Property(x => x.OrderId).HasMaxLength(64);
        b.Property(x => x.PaymentId).HasMaxLength(64);
        b.Property(x => x.RefundId).HasMaxLength(64);
        b.Property(x => x.SettlementId).HasMaxLength(64);
        b.Property(x => x.Note).HasMaxLength(300);

        // A redelivered event finds its first delivery; a concurrent one runs into this index.
        b.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        b.HasIndex(x => x.ReceivedAt);
    }
}
