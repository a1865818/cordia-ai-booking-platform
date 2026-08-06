using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>
/// EF Core configuration for <see cref="TenantDisplayName"/> → <c>tenant_display_names</c>.
///
/// <c>ITenantScoped</c>, so it inherits the global query filter like every other tenant table —
/// one tenant must never read another's venue name, which is guest-facing copy.
/// </summary>
public class TenantDisplayNameConfiguration : IEntityTypeConfiguration<TenantDisplayName>
{
    public void Configure(EntityTypeBuilder<TenantDisplayName> builder)
    {
        // One name per tenant: the tenant IS the key, so a second row is impossible rather
        // than merely discouraged.
        builder.HasKey(e => e.TenantId);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
