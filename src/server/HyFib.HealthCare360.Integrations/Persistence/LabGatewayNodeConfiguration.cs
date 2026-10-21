using HyFib.HealthCare360.Integrations.Domain;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Integrations.Persistence;

internal sealed class LabGatewayNodeConfiguration : IEntityTypeConfiguration<LabGatewayNode>
{
    public void Configure(EntityTypeBuilder<LabGatewayNode> b)
    {
        b.ToTable("lab_gateway_nodes", IntegrationsSchema.Name);
        b.HasKey(x => x.Node);
        b.Property(x => x.Node).HasMaxLength(64);
        b.Property(x => x.Statuses).HasColumnType("jsonb");
        b.Property(x => x.RelayingAgents).HasColumnType("jsonb");
        b.Property(x => x.Worklist).HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
    }
}
