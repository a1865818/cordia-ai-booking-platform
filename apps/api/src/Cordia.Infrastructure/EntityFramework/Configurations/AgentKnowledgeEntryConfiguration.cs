using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="AgentKnowledgeEntry"/> → <c>agent_knowledge_entries</c>.</summary>
public class AgentKnowledgeEntryConfiguration : IEntityTypeConfiguration<AgentKnowledgeEntry>
{
    public void Configure(EntityTypeBuilder<AgentKnowledgeEntry> builder)
    {
        builder.ToTable("agent_knowledge_entries");

        builder.HasKey(e => new { e.TenantId, e.EntryId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.EntryId).IsRequired();
        builder.Property(e => e.Question).IsRequired();
        builder.Property(e => e.Status)
            .HasColumnType("knowledge_status_enum")
            .IsRequired();
        builder.Property(e => e.Source).IsRequired().HasMaxLength(32);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
