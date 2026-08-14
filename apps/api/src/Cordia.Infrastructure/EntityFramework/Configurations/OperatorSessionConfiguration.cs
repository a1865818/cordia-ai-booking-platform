using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="OperatorSession"/> → <c>operator_sessions</c>.</summary>
public class OperatorSessionConfiguration : IEntityTypeConfiguration<OperatorSession>
{
    public void Configure(EntityTypeBuilder<OperatorSession> builder)
    {
        builder.ToTable("operator_sessions");

        builder.HasKey(e => new { e.TenantId, e.SessionId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.SessionId).IsRequired();
        builder.Property(e => e.OperatorId).IsRequired();
        builder.Property(e => e.IssuedAt).IsRequired();
        builder.Property(e => e.ExpiresAt).IsRequired();

        builder.HasOne<Operator>()
            .WithMany()
            .HasForeignKey(e => new { e.TenantId, e.OperatorId })
            .HasPrincipalKey(nameof(Operator.TenantId), nameof(Operator.OperatorId))
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.SessionId)
            .HasDatabaseName("ix_operator_sessions_session_id");
    }
}
