using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="WaitlistEnrollment"/>.</summary>
public class WaitlistEnrollmentConfiguration : IEntityTypeConfiguration<WaitlistEnrollment>
{
    public void Configure(EntityTypeBuilder<WaitlistEnrollment> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.EnrollmentId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.EnrollmentId).IsRequired();
        builder.Property(e => e.CustomerRef).IsRequired();
        builder.Property(e => e.LocationId).IsRequired();
        builder.Property(e => e.ServiceId).IsRequired();
        builder.Property(e => e.NotificationChannel).IsRequired();

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(e => e.DepositTerms).HasColumnType("jsonb");
        builder.Property(e => e.AutoBook).HasColumnType("jsonb");

        // Spec 11 §5.1 (divergence #1) — identity context for the accept→revision mint.
        // Migration generated in Phase 6 (see divergence-fixes plan).
        builder.Property(e => e.IdentityRef).HasColumnName("identity_ref").HasDefaultValue(string.Empty);
        builder.Property(e => e.VerificationLevel).HasColumnName("verification_level").HasDefaultValue("unverified");
    }
}
