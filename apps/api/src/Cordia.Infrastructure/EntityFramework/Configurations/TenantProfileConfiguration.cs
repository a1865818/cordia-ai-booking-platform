using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="TenantProfile"/> → <c>tenant_profiles</c>.</summary>
public class TenantProfileConfiguration : IEntityTypeConfiguration<TenantProfile>
{
    public void Configure(EntityTypeBuilder<TenantProfile> builder)
    {
        builder.ToTable("tenant_profiles");

        builder.HasKey(e => e.TenantId);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.BusinessType).HasMaxLength(32);
        builder.Property(e => e.WaitlistAutoBook).HasDefaultValue(false);
        builder.Property(e => e.HoursJson).HasColumnType("jsonb");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
