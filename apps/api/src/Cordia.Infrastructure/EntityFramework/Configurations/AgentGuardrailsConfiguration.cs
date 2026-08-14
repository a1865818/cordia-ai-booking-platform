using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="AgentGuardrails"/> → <c>agent_guardrails</c>.</summary>
public class AgentGuardrailsConfiguration : IEntityTypeConfiguration<AgentGuardrails>
{
    public void Configure(EntityTypeBuilder<AgentGuardrails> builder)
    {
        builder.ToTable("agent_guardrails");

        builder.HasKey(e => e.TenantId);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.TogglesJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
