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

    public Task<WaitlistOffer?> GetAsync(string offerId, CancellationToken ct = default)
        => db.WaitlistOffers
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.OfferId == offerId, ct);

    public Task<WaitlistOffer?> GetLiveByEnrollmentAsync(string enrollmentId, CancellationToken ct = default)
        => db.WaitlistOffers
            .AsNoTracking()
            .Where(o => o.EnrollmentId == enrollmentId
                && (o.Status == WaitlistOfferStatus.Pending
                    || o.Status == WaitlistOfferStatus.Matched
                    || o.Status == WaitlistOfferStatus.Offered))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

    public Task<int> TryTransitionStatusAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        WaitlistOfferStatus expectedStatus,
        WaitlistOfferStatus next,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WaitlistOffer>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.OfferId == offerId && e.Status == expectedStatus,
            s => s.SetProperty(x => x.Status, next),
            ct);

    public Task<int> TryOfferAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        DateTimeOffset offeredAt,
        DateTimeOffset offerExpiresAt,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WaitlistOffer>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.OfferId == offerId && e.Status == WaitlistOfferStatus.Matched,
            s => s
                .SetProperty(x => x.Status, WaitlistOfferStatus.Offered)
                .SetProperty(x => x.OfferedAt, offeredAt)
                .SetProperty(x => x.OfferExpiresAt, offerExpiresAt),
            ct);

    public Task<int> TryResolveAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        WaitlistOfferStatus expectedStatus,
        WaitlistOfferStatus next,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<WaitlistOffer>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.OfferId == offerId && e.Status == expectedStatus,
            s => s
                .SetProperty(x => x.Status, next)
                .SetProperty(x => x.ResolvedAt, resolvedAt),
            ct);
}
