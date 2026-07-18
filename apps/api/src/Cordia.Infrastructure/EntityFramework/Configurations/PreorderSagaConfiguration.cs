using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="PreorderSaga"/>.</summary>
public class PreorderSagaConfiguration : IEntityTypeConfiguration<PreorderSaga>
{
    public void Configure(EntityTypeBuilder<PreorderSaga> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.SagaId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.SagaId).IsRequired();
        builder.Property(e => e.OperationId).IsRequired();

        builder.Property(e => e.Version)
            .IsConcurrencyToken()
            .HasDefaultValue(0L);

        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

        builder.Property(e => e.PreorderReference)
            .IsRequired()
            .HasColumnType("jsonb");

        builder.Property(e => e.IsStandalone)
            .HasDefaultValue(false);

        // One saga per operation (§5.3): the start is idempotent on the operation (GetByOperationAsync
        // pre-check), and this unique index is the DB backstop so two concurrent start deliveries can
        // never insert two sagas for the same operation (keeps operation-scoped lookups deterministic).
        builder.HasIndex(e => new { e.TenantId, e.OperationId })
            .HasDatabaseName("uq_preorder_saga_per_operation")
            .IsUnique();

        // FK (tenant_id, operation_id) → booking_operations ON DELETE RESTRICT
        builder.HasOne<BookingOperation>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperationId })
            .HasPrincipalKey("TenantId", "OperationId")
            .OnDelete(DeleteBehavior.Restrict);
    }
}
