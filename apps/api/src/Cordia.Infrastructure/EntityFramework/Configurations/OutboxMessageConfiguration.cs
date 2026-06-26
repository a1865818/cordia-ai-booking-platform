using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="OutboxMessage"/>.</summary>
public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.MessageId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.MessageId).IsRequired();
        builder.Property(e => e.MessageType).IsRequired();
        builder.Property(e => e.CorrelationId).IsRequired();
        builder.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.OccurredAt).HasDefaultValueSql("now()");
        builder.Property(e => e.PublishAttempts).HasDefaultValue(0);

        builder.HasIndex(e => new { e.TenantId, e.OccurredAt })
            .HasDatabaseName("ix_outbox_unpublished")
            .HasFilter("published_at IS NULL");
    }
}
