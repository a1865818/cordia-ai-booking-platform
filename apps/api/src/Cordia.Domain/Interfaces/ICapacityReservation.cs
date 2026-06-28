using Cordia.Domain.Capacity;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Domain service for atomic capacity reservation, idempotent release, and conditional
/// finalization of capacity holds (spec 07 §3, §4.5).
/// <para>
/// All methods are designed to run inside the caller's ambient EF Core transaction
/// (via <c>IUnitOfWork.ExecuteInTransactionAsync</c>) so that slot writes, hold writes,
/// and the operation status transition all commit atomically (Inv-4, Inv-6).
/// </para>
/// </summary>
public interface ICapacityReservation
{
    /// <summary>
    /// Atomically reserves <see cref="ReserveRequest.RequestedCapacity"/> units against the
    /// specified slot using a conditional UPDATE (spec 07 §4.3, Inv-6) and inserts an active
    /// <see cref="CapacityHold"/> row within the caller's transaction.
    /// <para>
    /// Returns <see cref="ReservationStatus.Held"/> with the created hold on success;
    /// <see cref="ReservationStatus.NoCapacity"/> when the slot is full (rows_affected == 0 due to
    /// insufficient remaining capacity, not a version race); or
    /// <see cref="ReservationStatus.Ambiguous"/> when an external adapter returns an ambiguous
    /// outcome (route to Reconciling, keyed by provider_ref — Inv-7).
    /// </para>
    /// </summary>
    Task<ReservationOutcome> ReserveAsync(ReserveRequest req, CancellationToken ct = default);

    /// <summary>
    /// Idempotently releases a single hold by ID. Safe to call multiple times: if the hold is
    /// already released, expired, or finalized (i.e. not Active), the call is a no-op (§14.3,
    /// Inv-5). When the hold is released, the corresponding slot's held capacity is decremented
    /// (only when <see cref="CapacityHold.SlotId"/> is non-null — external-only holds carry no
    /// first-party slot reference).
    /// </summary>
    Task ReleaseAsync(string tenantId, string holdId, ReleaseReason reason, CancellationToken ct = default);

    /// <summary>
    /// Releases all active holds for a given operation (used by the
    /// <c>capacity.hold_release.requested.v1</c> consumer driven by deposit expiry, spec 07 §4,
    /// architecture fact §deposit-expiry-seam). For each active hold, applies the same idempotent
    /// release logic as <see cref="ReleaseAsync"/>.
    /// </summary>
    /// <returns>Number of holds released (may be zero if no active holds exist).</returns>
    Task<int> ReleaseByOperationAsync(string tenantId, string operationId, ReleaseReason reason, CancellationToken ct = default);

    /// <summary>
    /// Conditionally transitions a single hold from Active to Finalized and atomically moves
    /// <see cref="CapacityHold.ReservedCapacity"/> units from held to booked on the associated
    /// first-party slot (when <see cref="CapacityHold.SlotId"/> is non-null), all within the
    /// caller's transaction (Inv-4).
    /// <para>
    /// Returns <see cref="FinalizeOutcome.Finalized"/> when rows_affected == 1 (the hold was
    /// active and is now finalized). Returns <see cref="FinalizeOutcome.HoldNoLongerValid"/> when
    /// rows_affected == 0 (the hold was already expired or released — expiry raced finalization and
    /// won; §14.3). The caller must route HoldNoLongerValid to Reconciling, not Booked, to avoid a
    /// double slot decrement.
    /// </para>
    /// </summary>
    Task<FinalizeOutcome> FinalizeAgainstHoldAsync(string tenantId, string holdId, CancellationToken ct = default);
}
