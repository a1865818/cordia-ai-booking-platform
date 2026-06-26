using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>
/// EF Core configuration for <see cref="TenantChannelEndpoint"/>.
/// No global query filter — entity is NOT ITenantScoped (cross-tenant ingress resolver).
/// </summary>
public class TenantChannelEndpointConfiguration : IEntityTypeConfiguration<TenantChannelEndpoint>
{
    public void Configure(EntityTypeBuilder<TenantChannelEndpoint> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.ChannelEndpointId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ChannelEndpointId).IsRequired();
        builder.Property(e => e.SignalValue).IsRequired();
        builder.Property(e => e.ConfigurationVersion).IsRequired();
        builder.Property(e => e.PolicyBundleId).IsRequired();

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

        // Inv-13: unique active signal globally across all tenants (no TenantId in index)
        builder.HasIndex(e => new { e.Channel, e.SignalType, e.SignalValue })
            .IsUnique()
            .HasDatabaseName("uq_channel_signal_active")
            .HasFilter("status = 'active'");
    }
}
