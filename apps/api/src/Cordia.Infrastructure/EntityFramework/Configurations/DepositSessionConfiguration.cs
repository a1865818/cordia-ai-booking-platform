using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="DepositSession"/>.</summary>
public class DepositSessionConfiguration : IEntityTypeConfiguration<DepositSession>
{
    public void Configure(EntityTypeBuilder<DepositSession> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.DepositSessionId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.DepositSessionId).IsRequired();
        builder.Property(e => e.OperationId).IsRequired();
        builder.Property(e => e.Currency).IsRequired();

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // FK (tenant_id, operation_id) → booking_operations ON DELETE RESTRICT
        builder.HasOne<BookingOperation>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperationId })
            .HasPrincipalKey("TenantId", "OperationId")
            .OnDelete(DeleteBehavior.Restrict);

        // UNIQUE: at most one deposit session per operation
        builder.HasIndex(e => new { e.TenantId, e.OperationId })
            .IsUnique()
            .HasDatabaseName("uq_deposit_session_per_operation");

        // Partial index for expiry sweep (only link_sent rows need scanning)
        builder.HasIndex(e => new { e.TenantId, e.ExpiresAt })
            .HasDatabaseName("ix_deposit_sessions_expiry")
            .HasFilter("status = 'link_sent'");
    }
}
