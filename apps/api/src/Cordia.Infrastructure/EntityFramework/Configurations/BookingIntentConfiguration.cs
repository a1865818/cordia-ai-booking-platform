using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="BookingIntent"/>.</summary>
public class BookingIntentConfiguration : IEntityTypeConfiguration<BookingIntent>
{
    public void Configure(EntityTypeBuilder<BookingIntent> builder)
    {
        builder.HasKey(e => new { e.TenantId, e.IntentId });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.IntentId).IsRequired();
        builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    }
}
