using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="VerificationGrant"/>.</summary>
public class VerificationGrantConfiguration : IEntityTypeConfiguration<VerificationGrant>
{
    public void Configure(EntityTypeBuilder<VerificationGrant> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.GrantId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.GrantId).IsRequired();
        builder.Property(e => e.CustomerRef).IsRequired();

        builder.Property(e => e.IssuedAt).HasDefaultValueSql("now()");

        // No FK: customer_ref and target_booking_id are loose cross-service refs (spec 12 §5.5).
        // No version token: consumed_at written once via ConsumeAsync (no concurrent transitions).
    }
}
