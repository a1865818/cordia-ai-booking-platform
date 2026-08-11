using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="Booking"/>.</summary>
public class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.BookingId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.BookingId).IsRequired();
        builder.Property(e => e.BookingReference).IsRequired();
        builder.Property(e => e.CustomerId).IsRequired();
        builder.Property(e => e.LifecycleStatus)
            .HasColumnType("booking_lifecycle_status_enum")
            .IsRequired();
        // Nullable and unindexed by design: it is read only alongside a booking already fetched by
        // key, never searched on, and an additive nullable column keeps this hot table's migration
        // non-blocking.
        builder.Property(e => e.PendingOperationId);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0L);

        builder.HasIndex(e => new { e.TenantId, e.BookingReference })
            .IsUnique();

        builder.HasIndex(e => new { e.TenantId, e.ProviderReference })
            .HasFilter("provider_reference IS NOT NULL");

        builder.HasIndex(e => new { e.TenantId, e.CustomerId });
    }
}
