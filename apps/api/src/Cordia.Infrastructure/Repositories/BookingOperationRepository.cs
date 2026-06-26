using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IBookingOperationRepository"/>.</summary>
public class BookingOperationRepository(ApplicationDbContext db) : IBookingOperationRepository
{
    public async Task AddAsync(BookingOperation operation, CancellationToken ct = default)
    {
        db.BookingOperations.Add(operation);
        await db.SaveChangesAsync(ct);
    }

    public Task<BookingOperation?> GetAsync(string operationId, CancellationToken ct = default)
        => db.BookingOperations
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OperationId == operationId, ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus expectedStatus,
        OperationStatus next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<BookingOperation>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.OperationId == operationId && e.Status == expectedStatus,
            s => s
                .SetProperty(x => x.Status, next)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
            ct);
}
