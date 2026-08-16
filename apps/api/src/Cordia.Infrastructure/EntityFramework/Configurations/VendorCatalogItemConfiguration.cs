using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="VendorCatalogItem"/>.</summary>
public class VendorCatalogItemConfiguration : IEntityTypeConfiguration<VendorCatalogItem>
{
    public void Configure(EntityTypeBuilder<VendorCatalogItem> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.ItemId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ItemId).IsRequired();
        builder.Property(e => e.CatalogId).IsRequired();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.Description).IsRequired();
        builder.Property(e => e.Currency).IsRequired();

        builder.Property(e => e.Modifiers).HasColumnType("jsonb");
        builder.Property(e => e.AvailabilityWindow).HasColumnType("jsonb");
        builder.Property(e => e.MenuMetadata).HasColumnType("jsonb");
        builder.Property(e => e.AllergenInfo).HasColumnType("jsonb");
        builder.Property(e => e.DurationMinutes);

        // FK (tenant_id, catalog_id) → vendor_catalogs ON DELETE RESTRICT
        builder.HasOne<VendorCatalog>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.CatalogId })
            .HasPrincipalKey("TenantId", "CatalogId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
