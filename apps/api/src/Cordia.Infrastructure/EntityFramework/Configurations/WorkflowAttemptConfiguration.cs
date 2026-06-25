using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="WorkflowAttempt"/> (append-only log; no FK, no version).</summary>
public class WorkflowAttemptConfiguration : IEntityTypeConfiguration<WorkflowAttempt>
{
    public void Configure(EntityTypeBuilder<WorkflowAttempt> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.AttemptId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.AttemptId).IsRequired();
        builder.Property(e => e.RequestMeta).HasColumnType("jsonb");
        builder.Property(e => e.ResponseMeta).HasColumnType("jsonb");
        builder.Property(e => e.Reconciliation).HasColumnType("jsonb");
    }
}
