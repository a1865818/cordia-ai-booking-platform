using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="BookingOperation"/>.</summary>
public class BookingOperationConfiguration : IEntityTypeConfiguration<BookingOperation>
{
    public void Configure(EntityTypeBuilder<BookingOperation> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.OperationId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.OperationId).IsRequired();
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0L);

        // FK to the exact intent revision (tenant_id, intent_id, intent_revision) → revisions PK
        builder.HasOne<BookingIntentRevision>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.IntentId, e.IntentRevision })
            .HasPrincipalKey("TenantId", "IntentId", "Revision")
            .OnDelete(DeleteBehavior.Restrict); // §9.4 no physical deletion outside privacy workflow
    }
}
