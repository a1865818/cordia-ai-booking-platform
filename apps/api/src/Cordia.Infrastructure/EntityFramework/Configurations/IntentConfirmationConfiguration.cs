using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="IntentConfirmation"/>.</summary>
public class IntentConfirmationConfiguration : IEntityTypeConfiguration<IntentConfirmation>
{
    public void Configure(EntityTypeBuilder<IntentConfirmation> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.ConfirmationId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.ConfirmationId).IsRequired();
        builder.Property(e => e.Disclosures).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.ConfirmedAt).HasDefaultValueSql("now()");

        // FK to the EXACT revision (§22.1, §8.8): composite FK → composite PK of revision
        builder.HasOne(e => e.Revision)
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.IntentId, e.IntentRevision })
            .HasPrincipalKey("TenantId", "IntentId", "Revision")
            .OnDelete(DeleteBehavior.Restrict); // §9.4 no physical deletion outside privacy workflow

        // Payload-hash binding (§22.1, §8.8): column-level NOT NULL check;
        // the cross-table invariant is enforced by the BEFORE INSERT trigger in the migration.
        builder.ToTable(
            "intent_confirmations",
            t => t.HasCheckConstraint("ck_confirmation_hash_binding", "intent_payload_hash is not null"));
    }
}
