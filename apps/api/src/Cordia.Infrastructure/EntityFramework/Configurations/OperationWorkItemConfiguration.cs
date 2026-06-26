using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="OperationWorkItem"/>.</summary>
public class OperationWorkItemConfiguration : IEntityTypeConfiguration<OperationWorkItem>
{
    public void Configure(EntityTypeBuilder<OperationWorkItem> builder)
    {
        // §22 names this table `operations_work_items` (plural); the default from the
        // entity/DbSet name would be `operation_work_items`, so pin it explicitly.
        builder.ToTable("operations_work_items");

        builder.HasKey(e => new { e.TenantId, e.WorkItemId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.WorkItemId).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0L);
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.Resolution).HasColumnType("jsonb");

        builder.HasOne<BookingOperation>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperationId })
            .HasPrincipalKey("TenantId", "OperationId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
