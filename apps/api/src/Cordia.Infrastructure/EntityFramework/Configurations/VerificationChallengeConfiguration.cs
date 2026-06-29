using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

public class VerificationChallengeConfiguration : IEntityTypeConfiguration<VerificationChallenge>
{
    public void Configure(EntityTypeBuilder<VerificationChallenge> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.VerificationId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.VerificationId).IsRequired();
        builder.Property(e => e.IdempotencyKey).IsRequired();
        builder.Property(e => e.Channel)
            .HasColumnType("verification_request_channel_enum")
            .IsRequired();
        builder.Property(e => e.CodeHash).IsRequired();
        builder.Property(e => e.RequestedAt).HasDefaultValueSql("now()");

        builder.HasIndex(e => new { e.TenantId, e.IdempotencyKey }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.ExpiresAt });
    }
}
