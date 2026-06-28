using Cordia.Domain.Capacity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="CapacitySlot"/> (spec 07 §4.2).</summary>
public class CapacitySlotConfiguration : IEntityTypeConfiguration<CapacitySlot>
{
    public void Configure(EntityTypeBuilder<CapacitySlot> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.SlotId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.SlotId).IsRequired();
        builder.Property(e => e.ResourceId).IsRequired();
        builder.Property(e => e.StartsAt).IsRequired();
        builder.Property(e => e.HeldCapacity).HasDefaultValue(0);
        builder.Property(e => e.BookedCapacity).HasDefaultValue(0);
        builder.Property(e => e.Version).HasDefaultValue(0L);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "ck_capacity_slots_nonneg",
                "total_capacity >= 0 AND held_capacity >= 0 AND booked_capacity >= 0");
            t.HasCheckConstraint(
                "ck_capacity_slots_no_overcommit",
                "held_capacity + booked_capacity <= total_capacity");
        });
    }
}
