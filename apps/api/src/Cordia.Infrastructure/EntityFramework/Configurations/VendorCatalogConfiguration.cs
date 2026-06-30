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

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);
    }
}
