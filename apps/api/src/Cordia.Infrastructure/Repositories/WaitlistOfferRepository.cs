using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IWaitlistOfferRepository"/>.</summary>
public class WaitlistOfferRepository(ApplicationDbContext db) : IWaitlistOfferRepository
{
    public async Task AddAsync(WaitlistOffer offer, CancellationToken ct = default)
    {
        db.WaitlistOffers.Add(offer);
        await db.SaveChangesAsync(ct);
    }

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        WaitlistOfferStatus next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WaitlistOffer>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.OfferId == offerId,
            s => s.SetProperty(x => x.Status, next),
            ct);
}
