using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IBookingIntentRepository"/>.</summary>
public class BookingIntentRepository(ApplicationDbContext db) : IBookingIntentRepository
{
    public async Task AddIntentAsync(BookingIntent intent, CancellationToken ct = default)
    {
        db.BookingIntents.Add(intent);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddRevisionAsync(BookingIntentRevision revision, CancellationToken ct = default)
    {
        db.BookingIntentRevisions.Add(revision);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddConfirmationAsync(IntentConfirmation confirmation, CancellationToken ct = default)
    {
        db.IntentConfirmations.Add(confirmation);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Filters explicitly on both <paramref name="tenantId"/> and <paramref name="intentId"/>
    /// in addition to the ambient global query filter (belt-and-suspenders tenant isolation).
    /// </remarks>
    public Task<bool> IntentExistsAsync(string tenantId, string intentId, CancellationToken ct = default)
        => db.BookingIntents.AnyAsync(i => i.TenantId == tenantId && i.IntentId == intentId, ct);
}
