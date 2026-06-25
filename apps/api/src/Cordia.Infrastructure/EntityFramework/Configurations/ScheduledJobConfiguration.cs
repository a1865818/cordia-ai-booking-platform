using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="ScheduledJob"/>.</summary>
public class ScheduledJobConfiguration : IEntityTypeConfiguration<ScheduledJob>
{
    public void Configure(EntityTypeBuilder<ScheduledJob> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.JobId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.JobId).IsRequired();
        builder.Property(e => e.JobType).IsRequired();
        builder.Property(e => e.DestinationQueue).IsRequired();
        builder.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
        builder.Property(e => e.Attempt).HasDefaultValue(0);

        builder.HasIndex(e => new { e.TenantId, e.RunAfter })
            .HasDatabaseName("ix_scheduled_jobs_due")
            .HasFilter("status = 'pending'");
    }
}
