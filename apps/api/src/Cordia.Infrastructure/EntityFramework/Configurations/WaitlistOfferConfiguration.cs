using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="WaitlistOffer"/>.</summary>
public class WaitlistOfferConfiguration : IEntityTypeConfiguration<WaitlistOffer>
{
    public void Configure(EntityTypeBuilder<WaitlistOffer> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.OfferId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.OfferId).IsRequired();
        builder.Property(e => e.EnrollmentId).IsRequired();

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // FK (tenant_id, enrollment_id) → waitlist_enrollments ON DELETE RESTRICT
        builder.HasOne<WaitlistEnrollment>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.EnrollmentId })
            .HasPrincipalKey("TenantId", "EnrollmentId")
            .OnDelete(DeleteBehavior.Restrict);

        // M6 / Inv-6: at most one LIVE offer per (tenant_id, enrollment_id). A concurrent double-match
        // (two openings racing to offer the same enrollee) violates this partial unique index instead
        // of taking two competing holds; the loser settles. Terminal states (accepted/declined/
        // expired/released) are excluded so the enrollee can be re-offered a later opening.
        builder.HasIndex(e => new { e.TenantId, e.EnrollmentId })
            .HasDatabaseName("uq_live_offer_per_enrollment")
            .IsUnique()
            .HasFilter("status IN ('pending', 'matched', 'offered')");
    }
}
