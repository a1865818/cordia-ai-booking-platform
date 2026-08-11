using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>
/// EF Core configuration for <see cref="GuestContact"/> → <c>guest_contacts</c>.
///
/// <c>ITenantScoped</c>, so it inherits the global query filter: guest contact is the most
/// sensitive non-payment data the platform holds and must never cross a tenant boundary (Inv-13).
/// </summary>
public class GuestContactConfiguration : IEntityTypeConfiguration<GuestContact>
{
    public void Configure(EntityTypeBuilder<GuestContact> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.ContactRef });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ContactRef).IsRequired().HasMaxLength(64);
        builder.Property(e => e.PhoneE164).HasMaxLength(32);
        builder.Property(e => e.Email).HasMaxLength(320);
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.Property(e => e.ContactFingerprint).IsRequired().HasMaxLength(64);
        builder.Property(e => e.PhoneFingerprint).HasMaxLength(64);
        builder.Property(e => e.EmailFingerprint).HasMaxLength(64);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.LastUsedAt).HasDefaultValueSql("now()");

        // Idempotent creation: one row per distinct contact per tenant, so a guest who books
        // fifty times has one erasable row rather than fifty.
        builder.HasIndex(e => new { e.TenantId, e.ContactFingerprint }).IsUnique();

        // Discovery lookup by phone. Indexed on the phone FINGERPRINT rather than the phone for the
        // reason on the entity: an index on the raw number is a second copy of it. NOT unique —
        // one guest may legitimately hold several rows for the same number (e.g. one with an email
        // and one without), and discovery wants all of them.
        builder.HasIndex(e => new { e.TenantId, e.PhoneFingerprint });
        builder.HasIndex(e => new { e.TenantId, e.EmailFingerprint });

        // The retention sweep's scan predicate. On LAST USE, not creation — see the entity.
        builder.HasIndex(e => new { e.TenantId, e.LastUsedAt });
    }
}
