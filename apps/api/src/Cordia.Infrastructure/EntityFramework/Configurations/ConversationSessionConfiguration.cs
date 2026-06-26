using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="ConversationSession"/>.</summary>
public class ConversationSessionConfiguration : IEntityTypeConfiguration<ConversationSession>
{
    public void Configure(EntityTypeBuilder<ConversationSession> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.SessionId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.SessionId).IsRequired();
        builder.Property(e => e.Version).IsConcurrencyToken().HasDefaultValue(0L);
        builder.Property(e => e.StartedAt).HasDefaultValueSql("now()");
    }
}
