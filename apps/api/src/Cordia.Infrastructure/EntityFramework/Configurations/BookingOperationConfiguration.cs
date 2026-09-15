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
        builder.Property(e => e.StatusReason);
        builder.Property(e => e.CancellationRequestedAt);
        // Spec 11 §5.3 — pre-order operations delegate booking to 06 but signal the saga instead of
        // emitting a customer outcome. Migration generated in Phase 6 (see divergence-fixes plan).
        builder.Property(e => e.IsPreorder).HasColumnName("is_preorder").HasDefaultValue(false);
        builder.Property(e => e.AttributedSource)
            .HasColumnType("attributed_source_enum");

        // #110 — the console's newest-activity-first scan (ConsoleReadModel.ScanAsync) orders by
        // (updated_at DESC, operation_id DESC) and pages by keyset over exactly that order. Without
        // this index every page sorted the tenant's whole operation set.
        builder.HasIndex(e => new { e.TenantId, e.UpdatedAt, e.OperationId })
            .IsDescending(false, true, true);

        // FK to the exact intent revision (tenant_id, intent_id, intent_revision) → revisions PK
        builder.HasOne<BookingIntentRevision>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.IntentId, e.IntentRevision })
            .HasPrincipalKey("TenantId", "IntentId", "Revision")
            .OnDelete(DeleteBehavior.Restrict); // §9.4 no physical deletion outside privacy workflow
    }
}
