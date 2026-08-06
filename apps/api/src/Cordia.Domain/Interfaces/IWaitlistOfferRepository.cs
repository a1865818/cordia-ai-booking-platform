using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="WaitlistOffer"/> aggregate (spec 11 §4.4, §5.1).</summary>
public interface IWaitlistOfferRepository
{
    Task AddAsync(WaitlistOffer offer, CancellationToken ct = default);

    /// <summary>Loads an offer by id (tenant-scoped via the global query filter), or <c>null</c> when absent.</summary>
    Task<WaitlistOffer?> GetAsync(string offerId, CancellationToken ct = default);

    /// <summary>
    /// Returns the single live offer (status Pending / Matched / Offered) for an enrollment, or
    /// <c>null</c> when none is live (Inv-1 guarantees at most one). Used by enrollment cancel.
    /// </summary>
    Task<WaitlistOffer?> GetLiveByEnrollmentAsync(string enrollmentId, CancellationToken ct = default);

    /// <summary>
    /// The operation ids of every ACCEPTED offer for an enrollment (usually zero or one).
    ///
    /// <para>Used to tell FULFILMENT from withdrawal, which the enrollment row cannot do on its
    /// own: both are recorded by stamping <c>CancelledAt</c> — the accept-reconcile path uses it to
    /// make a booked enrollee ineligible for a second offer, exactly as a cancel does.</para>
    ///
    /// <para>The offer alone is not the answer, which is why this returns ids rather than a bool.
    /// An offer is moved to <c>Accepted</c> BEFORE the booking is confirmed and has no transition
    /// out again, so an accepted offer whose booking then failed terminally leaves the enrollee
    /// correctly eligible. Only the operation's own status settles it.</para>
    /// </summary>
    Task<IReadOnlyList<string>> GetAcceptedOfferOperationIdsAsync(
        string tenantId, string enrollmentId, CancellationToken ct = default);
    /// <summary>
    /// The offer that owns an <c>op_wl_*</c> operation id, or null.
    ///
    /// A waitlist offer's notification carries that id even though no <c>booking_operations</c>
    /// row exists for it until accept — so this is the only way to get from a notification back
    /// to the enrollment that knows where to send it (spec 10 §13).
    /// </summary>
    Task<WaitlistOffer?> GetByOperationIdAsync(string operationId, CancellationToken ct = default);


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

    /// <summary>
    /// Version-token conditional <c>Matched → Offered</c> transition that also stamps
    /// <paramref name="offeredAt"/> + <paramref name="offerExpiresAt"/> atomically (§5.1).
    /// Returns rows-affected (1 = won, 0 = lost race).
    /// </summary>
    Task<int> TryOfferAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        DateTimeOffset offeredAt,
        DateTimeOffset offerExpiresAt,
        CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional terminal transition (<paramref name="expectedStatus"/> →
    /// <paramref name="next"/>, one of Accepted / Declined / Expired / Released) that also stamps
    /// <paramref name="resolvedAt"/> atomically (§5.1). Returns rows-affected (1 = won, 0 = lost race).
    /// </summary>
    Task<int> TryResolveAsync(
        string tenantId,
        string offerId,
        long expectedVersion,
        WaitlistOfferStatus expectedStatus,
        WaitlistOfferStatus next,
        DateTimeOffset resolvedAt,
        CancellationToken ct = default);
}
