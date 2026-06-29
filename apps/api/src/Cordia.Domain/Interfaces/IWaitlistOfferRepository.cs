using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="WaitlistOffer"/> aggregate.</summary>
public interface IWaitlistOfferRepository
{
    Task AddAsync(WaitlistOffer offer, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional transition (§6.2): sets <paramref name="next"/> status only when
    /// the row is in <paramref name="expectedStatus"/> AND at <paramref name="expectedVersion"/>.
    /// Returns rows-affected (1 = success, 0 = lost race / wrong state → caller should reload).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        WaitlistOfferStatus expectedStatus,
        WaitlistOfferStatus next,
        CancellationToken ct = default);
}
