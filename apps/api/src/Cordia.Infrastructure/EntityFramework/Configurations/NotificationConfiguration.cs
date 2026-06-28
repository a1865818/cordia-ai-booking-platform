using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="Notification"/>.</summary>
public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        // PK (tenant_id, notification_id) = §22.1 unique logical-dedup constraint (Inv-9).
        builder.HasKey(e => new { e.TenantId, e.NotificationId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.NotificationId).IsRequired();
        builder.Property(e => e.OperationId).IsRequired();
        builder.Property(e => e.RecipientRef).IsRequired();
        builder.Property(e => e.TemplateVersion).IsRequired();

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        // FK (tenant_id, operation_id) → booking_operations ON DELETE RESTRICT (§9.4)
        builder.HasOne<BookingOperation>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperationId })
            .HasPrincipalKey("TenantId", "OperationId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
