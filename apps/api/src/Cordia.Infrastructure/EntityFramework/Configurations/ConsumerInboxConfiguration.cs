using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="ConsumerInbox"/>.
/// PK is (consumer_name, tenant_id, message_id) — consumer FIRST per spec §5.4.</summary>
public class ConsumerInboxConfiguration : IEntityTypeConfiguration<ConsumerInbox>
{
    public void Configure(EntityTypeBuilder<ConsumerInbox> builder)
    {
        // Consumer FIRST — deliberate, documented exception to tenant-leading PK (spec §5.4 §22.1)
        builder.HasKey(e => new { e.ConsumerName, e.TenantId, e.MessageId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ConsumerName).IsRequired();
        builder.Property(e => e.MessageId).IsRequired();
        builder.Property(e => e.AdmittedAt).HasDefaultValueSql("now()");

        builder.HasIndex(e => new { e.TenantId, e.AdmittedAt })
            .HasDatabaseName("ix_inbox_stuck_admitted")
            .HasFilter("outcome = 'admitted'");
    }
}
