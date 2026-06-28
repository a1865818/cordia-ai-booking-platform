using Cordia.Domain.Capacity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="CapacityHold"/> (spec 07 §4.4).</summary>
public class CapacityHoldConfiguration : IEntityTypeConfiguration<CapacityHold>
{
    public void Configure(EntityTypeBuilder<CapacityHold> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.HoldId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.HoldId).IsRequired();
        builder.Property(e => e.OperationId).IsRequired();
        builder.Property(e => e.WorkflowStep).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.CreatedAt).IsRequired();
        builder.Property(e => e.ExpiresAt).IsRequired();
        builder.Property(e => e.Version).HasDefaultValue(0L);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "ck_capacity_holds_reserved_positive",
                "reserved_capacity > 0");
        });

        // Partial unique: at most one active hold per (tenant_id, operation_id, workflow_step) (§22.1, Inv-6).
        builder.HasIndex(e => new { e.TenantId, e.OperationId, e.WorkflowStep })
            .HasDatabaseName("uq_active_hold_per_op_step")
            .IsUnique()
            .HasFilter("status = 'active'");

        // Expiry sweep index: quickly locate active holds past their expiry.
        builder.HasIndex(e => e.ExpiresAt)
            .HasDatabaseName("ix_capacity_holds_expiry")
            .HasFilter("status = 'active'");
    }
}
