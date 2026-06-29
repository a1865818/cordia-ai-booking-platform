using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="BookingIntentRevision"/>.</summary>
public class BookingIntentRevisionConfiguration : IEntityTypeConfiguration<BookingIntentRevision>
{
    public void Configure(EntityTypeBuilder<BookingIntentRevision> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.IntentId, e.Revision });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.IntentId).IsRequired();
        builder.Property(e => e.CanonicalPayload).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // §9.4: these rows are authoritative/append-only and must not be physically
        // deleted outside the privacy-retention workflow — Restrict so a parent delete
        // can never silently cascade-wipe the evidence chain.
        builder.HasOne(e => e.Intent)
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.IntentId })
            .HasPrincipalKey(e => new { e.TenantId, e.IntentId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
