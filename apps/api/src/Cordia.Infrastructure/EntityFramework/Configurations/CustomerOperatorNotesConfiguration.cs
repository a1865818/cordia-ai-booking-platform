using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordia.Infrastructure.EntityFramework.Configurations;

/// <summary>EF Core configuration for <see cref="CustomerOperatorNotes"/> → <c>customer_operator_notes</c>.</summary>
public class CustomerOperatorNotesConfiguration : IEntityTypeConfiguration<CustomerOperatorNotes>
{
    public void Configure(EntityTypeBuilder<CustomerOperatorNotes> builder)
    {
        builder.ToTable("customer_operator_notes");

        builder.HasKey(e => new { e.TenantId, e.CustomerRef });

        builder.Property(e => e.TenantId).IsRequired();
        builder.Property(e => e.CustomerRef).IsRequired();
        builder.Property(e => e.Note).IsRequired().HasDefaultValue("");
        builder.Property(e => e.TagsJson).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        builder.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    }
}
