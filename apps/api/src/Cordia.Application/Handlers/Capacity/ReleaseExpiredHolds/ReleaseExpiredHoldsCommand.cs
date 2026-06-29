using Cordia.Application.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;

/// <summary>
/// Determines how an expiry/release command targets capacity holds (spec 07 FIX 5).
/// </summary>
public enum ReleaseScope
{
    /// <summary>
    /// Release a single hold by <see cref="ReleaseExpiredHoldsCommand.HoldId"/>.
    /// Used by <c>booking.scheduled.hold_expiry.v1</c>. If HoldId is missing or null,
    /// the handler FAILS CLOSED (no bulk release — prevents accidental mass release on malformed payload).
    /// </summary>
    Hold,

    /// <summary>
    /// Release all active holds for <see cref="ReleaseExpiredHoldsCommand.OperationId"/>.
    /// Used by <c>capacity.hold_release.requested.v1</c> (deposit-expiry bulk-release path).
    /// </summary>
    Operation,
}

/// <summary>
/// Queue-driven command that idempotently releases capacity holds on expiry (spec 07 §7, §14.3).
///
/// <para>This command serves two delivery paths:</para>
/// <list type="bullet">
///   <item>
///     <c>booking.scheduled.hold_expiry.v1</c> (scheduled job from T5 <c>CapacityExpiryScheduler</c>):
///     <see cref="HoldId"/> is present — releases exactly one active hold by ID. This fires when the
///     hold TTL elapses after a successful <c>ReservingCapacity → Held</c> transition.
///   </item>
///   <item>
///     <c>capacity.hold_release.requested.v1</c> (event emitted by <c>ProcessDepositEventCommandHandler</c>
///     on the <c>AwaitingPayment → Expired</c> path — spec 06 §10 deposit-expiry seam): <see cref="HoldId"/>
///     is <see langword="null"/> — releases all active holds for the operation keyed by
///     <see cref="OperationId"/>.
///   </item>
/// </list>
///
/// <para><b>Idempotency:</b> <c>ICapacityReservation.ReleaseAsync</c> / <c>ReleaseByOperationAsync</c>
/// only act on holds with <c>status = active</c>. A duplicate delivery of the same
/// <see cref="MessageId"/> is suppressed at the inbox level (<c>InboxDeduplicationBehavior</c>) and
/// at the storage level (UPDATE WHERE status='active' affects 0 rows — §14.3). Both are no-op
/// successes.</para>
///
/// <para><b>Op-status transitions:</b> this handler is the CAPACITY concern only — it frees the
/// hold/slot. It does NOT force any operation-status transition. Per §11, <c>Held</c> has no legal
/// <c>→ Expired</c> edge; op-level expiry transitions on the deposit path are already driven by
/// <c>ProcessDepositEventCommandHandler</c> (<c>AwaitingPayment → Expired</c>). If an op is still
/// <c>Held</c> when the scheduled TTL fires (rare: driver stalled), the SLA sweeper handles it; no
/// illegal edge is invented here (see also T7's fail-closed finalize which routes
/// <c>HoldNoLongerValid → Reconciling</c>).</para>
///
/// <para><b>Transaction:</b> implements <see cref="IInboxConsumerRequest"/> so
/// <c>InboxDeduplicationBehavior</c> wraps all writes in ONE transaction. The handler MUST NOT open
/// its own transaction — it shares the ambient scoped <c>DbContext</c>.</para>
/// </summary>
public sealed record ReleaseExpiredHoldsCommand(
    string TenantId,
    string OperationId,
    string? HoldId,
    string MessageId,
    ReleaseScope Scope = ReleaseScope.Hold)
    : IRequest<Unit>, IInboxConsumerRequest
{
    public string ConsumerName => "capacity.release";
    string? IInboxConsumerRequest.CorrelationId => OperationId;
    string? IInboxConsumerRequest.OperationId => OperationId;
}
