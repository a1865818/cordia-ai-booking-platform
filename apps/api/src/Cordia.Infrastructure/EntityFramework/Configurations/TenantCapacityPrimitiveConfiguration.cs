using Cordia.Domain.Capacity;
using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="TenantCapacityPrimitive"/> → <c>tenant_capacity_primitives</c>.</summary>
public class TenantCapacityPrimitiveConfiguration : IEntityTypeConfiguration<TenantCapacityPrimitive>
{
    /// <summary>Wire names for the rungs. Text, not a pg-enum: spec 12 forbids inventing enum labels.</summary>
    internal static readonly IReadOnlyDictionary<CapacityPrimitive, string> WireNames =
        new Dictionary<CapacityPrimitive, string>
        {
            [CapacityPrimitive.FirstParty] = "first_party",
            [CapacityPrimitive.NativeHold] = "native_hold",
            [CapacityPrimitive.IdempotentPending] = "idempotent_pending",
            [CapacityPrimitive.AtomicBook] = "atomic_book",
            [CapacityPrimitive.CheckThenCreate] = "check_then_create",
        };

    private static CapacityPrimitive FromWire(string value)
        => WireNames.Single(kv => kv.Value == value).Key;

    public void Configure(EntityTypeBuilder<TenantCapacityPrimitive> builder)
    {
        builder.ToTable("tenant_capacity_primitives", t =>
        {
            t.HasCheckConstraint(
                "ck_tenant_capacity_primitives_primitive",
                "primitive IN ('first_party', 'native_hold', 'idempotent_pending', 'atomic_book', 'check_then_create')");
            // First-party inventory IS the conditional UPDATE on capacity_slots; it has no other
            // rung. A row claiming otherwise would make a first-party hold report "confirmed".
            t.HasCheckConstraint(
                "ck_tenant_capacity_primitives_first_party_rung",
                $"provider_name <> '{CapacityProviders.FirstParty}' OR primitive = 'first_party'");
        });

        builder.HasKey(e => new { e.TenantId, e.ProviderName });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ProviderName).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Primitive)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion(v => WireNames[v], v => FromWire(v));
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
