using HyFib.HealthCare360.Integrations.Domain;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Integrations.Persistence;

internal static class IntegrationsSchema
{
    public const string Name = "integrations";
}

internal sealed class SimulatorMessageConfiguration : IEntityTypeConfiguration<SimulatorMessage>
{
    public void Configure(EntityTypeBuilder<SimulatorMessage> b)
    {
        b.ToTable("simulator_messages", IntegrationsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Recipient).HasMaxLength(256);
        b.Property(x => x.Subject).HasMaxLength(300);
        b.Property(x => x.Body).HasMaxLength(8000);
        b.Property(x => x.Details).HasMaxLength(2000);
        b.Property(x => x.Reference).HasMaxLength(128);
        b.Property(x => x.ProviderMessageId).HasMaxLength(64);
        b.HasIndex(x => new { x.Channel, x.CreatedAt });
        b.HasIndex(x => x.ProviderMessageId);
    }
}

internal sealed class SimulatorTransactionConfiguration : IEntityTypeConfiguration<SimulatorTransaction>
{
    public void Configure(EntityTypeBuilder<SimulatorTransaction> b)
    {
        b.ToTable("simulator_transactions", IntegrationsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalId).HasMaxLength(64);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Reference).HasMaxLength(128);
        b.Property(x => x.TerminalId).HasMaxLength(32);
        b.Property(x => x.PaymentId).HasMaxLength(64);
        b.Property(x => x.ApprovalCode).HasMaxLength(16);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.SettlementId).HasMaxLength(64);
        b.HasIndex(x => x.ExternalId).IsUnique();
        b.HasIndex(x => x.PaymentId);
        b.HasIndex(x => new { x.Kind, x.CreatedAt });
        b.HasIndex(x => x.SettledAt);
    }
}

internal sealed class InboundWhatsAppMessageConfiguration : IEntityTypeConfiguration<InboundWhatsAppMessage>
{
    /// <summary>The unique index a concurrent webhook redelivery runs into (the name EF's conventions give it).</summary>
    public const string ProviderMessageIdIndex = "ix_whatsapp_inbound_messages_provider_message_id";

    public void Configure(EntityTypeBuilder<InboundWhatsAppMessage> b)
    {
        b.ToTable("whatsapp_inbound_messages", IntegrationsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ProviderMessageId).HasMaxLength(128);
        b.Property(x => x.FromPhone).HasMaxLength(20);
        b.Property(x => x.ProfileName).HasMaxLength(128);
        b.Property(x => x.MessageType).HasMaxLength(32);
        b.Property(x => x.Text).HasMaxLength(4096);
        b.Property(x => x.MediaId).HasMaxLength(128);
        b.Property(x => x.ContextMessageId).HasMaxLength(128);
        b.Property(x => x.PhoneNumberId).HasMaxLength(32);
        b.Ignore(x => x.RepliedAt);
        b.HasIndex(x => x.ProviderMessageId).IsUnique().HasDatabaseName(ProviderMessageIdIndex);
        b.HasIndex(x => new { x.FromPhone, x.ReceivedAt });
        b.HasIndex(x => x.ReceivedAt);
    }
}
