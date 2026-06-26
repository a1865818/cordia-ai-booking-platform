using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="DeadLetterMessage"/>.</summary>
public class DeadLetterMessageConfiguration : IEntityTypeConfiguration<DeadLetterMessage>
{
    public void Configure(EntityTypeBuilder<DeadLetterMessage> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.MessageId, e.ConsumerName });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.MessageId).IsRequired();
        builder.Property(e => e.ConsumerName).IsRequired();
        builder.Property(e => e.MessageType).IsRequired();
        builder.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.ErrorClass).IsRequired();
        builder.Property(e => e.AttemptHistory).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.FirstFailedAt).IsRequired();
        builder.Property(e => e.DeadLetteredAt).HasDefaultValueSql("now()");
    }
}
