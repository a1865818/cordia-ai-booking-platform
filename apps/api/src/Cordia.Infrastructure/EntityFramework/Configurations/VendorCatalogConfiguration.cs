using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="VendorCatalog"/>.</summary>
public class VendorCatalogConfiguration : IEntityTypeConfiguration<VendorCatalog>
{
    public void Configure(EntityTypeBuilder<VendorCatalog> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.CatalogId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CatalogId).IsRequired();
        builder.Property(e => e.VersionLabel).IsRequired();
        builder.Property(e => e.SourceHash).IsRequired();

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // Defaulted in SQL like CreatedAt, so an insert that does not set it still starts the
        // staleness clock at row birth rather than at DateTimeOffset.MinValue — which would make
        // every freshly uploaded catalog instantly eligible for extract_failed.
        builder.Property(e => e.StatusChangedAt).HasDefaultValueSql("now()");

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        // M4 / Inv-16: at most one published catalog per tenant. The publish transition is the only
        // writer of status='published'; this partial unique index makes a concurrent double-publish
        // fail at the database instead of leaving two live menus — the second publish loses.
        builder.HasIndex(e => e.TenantId)
            .HasDatabaseName("uq_published_catalog_per_tenant")
            .IsUnique()
            .HasFilter("status = 'published'");
    }
}
