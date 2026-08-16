using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="HandoffWorkItem"/>.</summary>
public class HandoffWorkItemConfiguration : IEntityTypeConfiguration<HandoffWorkItem>
{
    public void Configure(EntityTypeBuilder<HandoffWorkItem> builder)
    {
        builder.ToTable("handoff_work_items");

        builder.HasKey(e => new { e.TenantId, e.WorkItemId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.WorkItemId).IsRequired();
        builder.Property(e => e.SessionId).IsRequired();
        builder.Property(e => e.ReasonCode).IsRequired();
        builder.Property(e => e.HandoffIdempotencyKey).IsRequired();
        builder.Property(e => e.Queue).IsRequired();
        builder.Property(e => e.State).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0L);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // Inv-5/Inv-9 DB backstop. The handler looks the key up first, but a
        // concurrent reconnect can race between that read and the insert — the
        // agent's SingleFlight only guards one process. The unique index is
        // what makes "repeated calls with the same key return existing without
        // creating duplicates" true rather than merely likely.
        builder.HasIndex(e => new { e.TenantId, e.HandoffIdempotencyKey })
            .IsUnique()
            .HasDatabaseName("uq_handoff_idempotency_key");

        builder.HasIndex(e => new { e.TenantId, e.SessionId })
            .HasDatabaseName("ix_handoff_work_items_session");
    }
}
