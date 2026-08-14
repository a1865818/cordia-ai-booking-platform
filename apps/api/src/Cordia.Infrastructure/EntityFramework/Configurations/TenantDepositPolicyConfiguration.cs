using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="TenantDepositPolicy"/> → <c>tenant_deposit_policies</c>.</summary>
public class TenantDepositPolicyConfiguration : IEntityTypeConfiguration<TenantDepositPolicy>
{
    public void Configure(EntityTypeBuilder<TenantDepositPolicy> builder)
    {
        builder.ToTable("tenant_deposit_policies");

        builder.HasKey(e => e.TenantId);

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.AmountPolicy).IsRequired().HasMaxLength(32);
        builder.Property(e => e.RefundWindow).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Currency).HasMaxLength(8).HasDefaultValue("AUD");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
