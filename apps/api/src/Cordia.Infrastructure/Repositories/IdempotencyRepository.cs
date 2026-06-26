using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IIdempotencyRepository"/>.</summary>
public class IdempotencyRepository(ApplicationDbContext db) : IIdempotencyRepository
{
    public async Task AddAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        db.IdempotencyRecords.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public Task<IdempotencyRecord?> GetByKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.IdempotencyKey == idempotencyKey, ct);
}
