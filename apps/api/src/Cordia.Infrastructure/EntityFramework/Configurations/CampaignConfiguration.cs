using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="Campaign"/> → <c>campaigns</c>.</summary>
public class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> builder)
    {
        builder.ToTable("campaigns");

        builder.HasKey(e => new { e.TenantId, e.CampaignId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CampaignId).IsRequired();
        builder.Property(e => e.Title).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Status)
            .HasColumnType("campaign_status_enum")
            .HasDefaultValue(CampaignStatus.Awaiting)
            .IsRequired();
        builder.Property(e => e.AudienceDescription).IsRequired();
        builder.Property(e => e.Channel)
            .HasColumnType("channel_enum")
            .IsRequired();
        builder.Property(e => e.Message).IsRequired();
        builder.Property(e => e.ApprovedBy).HasMaxLength(64);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
