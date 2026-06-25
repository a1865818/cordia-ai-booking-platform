using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="WorkflowStep"/>.</summary>
public class WorkflowStepConfiguration : IEntityTypeConfiguration<WorkflowStep>
{
    public void Configure(EntityTypeBuilder<WorkflowStep> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.WorkflowStepId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.WorkflowStepId).IsRequired();
        builder.Property(e => e.AttemptCount).HasDefaultValue(0);
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0L);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(e => new { e.TenantId, e.OperationId, e.StepType })
            .IsUnique()
            .HasDatabaseName("uq_workflow_step_per_type");

        builder.HasOne<BookingOperation>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperationId })
            .HasPrincipalKey("TenantId", "OperationId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
