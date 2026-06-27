using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// Wraps multiple repository operations in a single <see cref="ApplicationDbContext"/> transaction
/// (spec 04 §10.3, Inv-4). All repositories injected in the same request scope share this DbContext,
/// so calling their AddAsync (each does its own SaveChangesAsync) inside <c>work</c> makes those
/// writes atomic — they flush within the transaction and only durably commit at CommitAsync.
/// </summary>
public sealed class UnitOfWork(ApplicationDbContext db) : IUnitOfWork
{
    /// <inheritdoc/>
    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await work(ct);            // multiple repo SaveChanges calls participate in THIS transaction
        await tx.CommitAsync(ct);  // only here is anything durably committed
    }
}
