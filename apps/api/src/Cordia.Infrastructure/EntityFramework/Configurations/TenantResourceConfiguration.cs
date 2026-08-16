using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="TenantResource"/>.</summary>
public class TenantResourceConfiguration : IEntityTypeConfiguration<TenantResource>
{
    public void Configure(EntityTypeBuilder<TenantResource> builder)
    {
        builder.ToTable("tenant_resources");

        // Kind is part of the key: a service and a location may legitimately
        // share an id (a venue whose "rooftop" is both a room and a sitting).
        builder.HasKey(e => new { e.TenantId, e.Kind, e.ResourceId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.ResourceId).IsRequired();
        builder.Property(e => e.Name).IsRequired();
        builder.Property(e => e.Active).HasDefaultValue(true);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // text[] rather than jsonb: these are matched, not documents, and the
        // provider maps List<string> to a native array without a converter.
        builder.Property(e => e.Aliases).HasColumnType("text[]");

        builder.HasIndex(e => new { e.TenantId, e.Kind })
            .HasDatabaseName("ix_tenant_resources_kind");
    }
}
