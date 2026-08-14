using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="Operator"/> → <c>operators</c>.</summary>
public class OperatorConfiguration : IEntityTypeConfiguration<Operator>
{
    public void Configure(EntityTypeBuilder<Operator> builder)
    {
        builder.ToTable("operators");

        builder.HasKey(e => new { e.TenantId, e.OperatorId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.OperatorId).IsRequired();
        builder.Property(e => e.Email).IsRequired().HasMaxLength(320);
        builder.Property(e => e.PasswordHash).IsRequired();
        builder.Property(e => e.Role).IsRequired().HasMaxLength(32);
        builder.Property(e => e.Status).IsRequired().HasMaxLength(32).HasDefaultValue("active");
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(e => new { e.TenantId, e.Email })
            .IsUnique()
            .HasDatabaseName("uq_operators_tenant_email");
    }
}
