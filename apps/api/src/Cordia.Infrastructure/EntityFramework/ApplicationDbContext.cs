using Cordia.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.EntityFramework;

/// <summary>EF Core context for the Cordia API.</summary>
public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<SampleEntity> Samples => Set<SampleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("public");

        modelBuilder.Entity<SampleEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired();
        });

        base.OnModelCreating(modelBuilder);
    }
}
