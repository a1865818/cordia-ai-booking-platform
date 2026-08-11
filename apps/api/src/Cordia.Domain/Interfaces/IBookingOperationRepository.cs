using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for booking operations; version-token transitions use the §22.1 helper.</summary>
public interface IBookingOperationRepository
{
    Task AddAsync(BookingOperation operation, CancellationToken ct = default);

    Task<BookingOperation?> GetAsync(string operationId, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE (§6.2): transitions to <paramref name="next"/> only when
    /// the row is in <paramref name="expectedStatus"/> AND at <paramref name="expectedVersion"/>.
    /// Guarding both the expected status and version makes an illegal state-graph transition
    /// return 0 even if versions coincide. Returns rows-affected (1 = won race, 0 = lost race → reload).
    /// <para>When <paramref name="bookingId"/> is non-null it is set on the row; otherwise the existing
    /// value is left unchanged (do not clobber a BookingId written by an earlier transition).</para>
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string operationId,
        long expectedVersion,
        OperationStatus expectedStatus,
        OperationStatus next,
        string? reason = null,
        string? bookingId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sets <c>CancellationRequestedAt</c> to <paramref name="when"/> on a non-terminal operation
    /// where the flag has not yet been set (spec 06 §15.5, FM#13).
    ///
    /// Applied only where: tenant_id matches, operation_id matches, cancellation_requested_at IS NULL,
    /// and status NOT IN (Booked, Rejected, Expired, Waitlisted, FailedFinal).
    ///
    /// Returns rows-affected (1 if newly set, 0 if already set or terminal).
    /// Does NOT bump Version — this is a flag, not a state transition.
    /// </summary>
    /// <summary>
    /// Records the booking a mutation targets, ONLY when the row does not already carry one.
    ///
    /// <para>Acceptance began stamping <c>booking_id</c> onto the operation later than the
    /// canonical payload began carrying <c>target_booking_id</c>, so operations accepted before
    /// that resolve their target from the payload at fulfilment time. Every recovery path
    /// downstream — reconciliation, manual review — reads the OPERATION row, so a target that
    /// lives only in a local variable is invisible to them. This is how the fallback becomes
    /// durable.</para>
    ///
    /// <para>Conditional on the current value being null so it can never overwrite a target
    /// another writer established, and idempotent under redelivery. Does NOT bump Version — this
    /// backfills a missing link, it is not a state transition.</para>
    /// </summary>
    /// <returns>Rows affected: 1 when newly set, 0 when already populated or absent.</returns>
    Task<int> TrySetBookingIdAsync(
        string tenantId,
        string operationId,
        string bookingId,
        CancellationToken ct = default);

    Task<int> SetCancellationRequestedAsync(
        string tenantId,
        string operationId,
        DateTimeOffset when,
        CancellationToken ct = default);

    /// <summary>
    /// Cross-tenant scan (IgnoreQueryFilters): returns up to <paramref name="limit"/> non-terminal
    /// operations that are NOT in a user-wait state, ordered by UpdatedAt asc. Used by the SLA evaluator.
    /// </summary>
    /// <summary>
    /// The operation that actually created <paramref name="bookingId"/> — the one whose capacity
    /// hold the booking occupies. Needed when cancelling, because the cancellation is a
    /// <i>different</i> operation and holds no capacity of its own (spec 08 §5.3).
    /// </summary>
    /// <returns>The booked create/reschedule operation for this booking, or null if none.</returns>
    Task<BookingOperation?> GetBookedOperationForBookingAsync(
        string tenantId,
        string bookingId,
        CancellationToken ct = default);

    Task<IReadOnlyList<BookingOperation>> ListNonTerminalForSlaAsync(
        DateTimeOffset now, int limit, CancellationToken ct = default);
}
