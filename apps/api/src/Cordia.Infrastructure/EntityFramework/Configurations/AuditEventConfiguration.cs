using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="AuditEvent"/>.</summary>
public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.EventId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.EventId).IsRequired();
        builder.Property(e => e.EventType).IsRequired();
        builder.Property(e => e.Actor).IsRequired();
        builder.Property(e => e.SubjectRef).IsRequired();

        builder.Property(e => e.Metadata)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(e => e.OccurredAt).HasDefaultValueSql("now()");

        // Append-only: no version token, no FK (all refs are loose cross-service refs).
    }
}
