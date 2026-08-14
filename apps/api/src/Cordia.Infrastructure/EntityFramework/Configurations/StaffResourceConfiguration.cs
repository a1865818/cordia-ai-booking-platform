using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="StaffResource"/> → <c>staff_resources</c>.</summary>
public class StaffResourceConfiguration : IEntityTypeConfiguration<StaffResource>
{
    public void Configure(EntityTypeBuilder<StaffResource> builder)
    {
        builder.ToTable("staff_resources");

        builder.HasKey(e => new { e.TenantId, e.ResourceId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ResourceId).IsRequired();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Role).HasMaxLength(64);
        builder.Property(e => e.Active).HasDefaultValue(true);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
