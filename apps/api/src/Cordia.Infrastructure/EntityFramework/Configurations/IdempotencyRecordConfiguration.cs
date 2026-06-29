using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="IdempotencyRecord"/>.</summary>
public class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.IdempotencyKey });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.IdempotencyKey).IsRequired();
        builder.Property(e => e.ResponseSnapshot).HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.LastSeenAt).HasDefaultValueSql("now()");
        builder.Property(e => e.RetentionDeadline).IsRequired();
    }
}
