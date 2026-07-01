using System.Collections.Frozen;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Domain.Preorder;

/// <summary>
/// Encodes the §5.3 allowed-edge set for the pre-order saga — the single authoritative source of
/// legal saga transitions in C#. The saga is a persistent state machine: each step advances
/// <see cref="PreorderSagaState"/> via a version-token guard, then enqueues the next (or a
/// compensation) step. Every edge below is either a forward step, a compensation (reverse-unwind)
/// step, or a timeout→reconcile / reconcile→resume recovery edge.
///
/// <para><b>Booking is delegated to spec-06.</b> The saga <em>orchestrates</em> but does not itself
/// hold capacity, authorize payment, or create the provider booking: the spec-06 operation engine
/// owns capacity (07), payment (09), and provider-create (06). The saga reserves the food items,
/// triggers the 06 operation, and awaits its terminal signal
/// (<c>preorder.commands.booking_committed.v1</c> / <c>booking_failed.v1</c>). This is why the
/// former saga-level <see cref="PreorderSagaState.HoldingCapacity"/> /
/// <see cref="PreorderSagaState.AuthorizingPayment"/> / <see cref="PreorderSagaState.CompensatingHold"/>
/// steps have left the forward edge set (the enum members are retained but unwired); 06 also owns the
/// ambiguous→reconcile guard (Inv-7) internally, so the saga only ever observes a terminal Booked vs
/// failed outcome.</para>
///
/// <para><b>Forward:</b> Revalidating → ReservingItems → CommittingBooking (await 06) →
/// CommittingPreorder → Completed.</para>
///
/// <para><b>Compensation</b> unwinds only what the saga itself acquired — the reserved items:
/// item-reserve failure or a 06 booking failure → CompensatingItems → FailedFinal; pre-order-commit
/// failure (the 06 booking already committed) → CompensatingBooking → ManualReview (operator +
/// customer, §5.3). <b>Reconciling</b> is the crash-recovery hub reachable from any in-flight forward
/// step on a step timeout, and either resumes the forward/compensation chain or escalates to a
/// terminal.</para>
///
/// <para><b>Terminals</b> (no outgoing edges): Completed, FailedFinal. <b>ManualReview</b> is a
/// human-resolution parking state (§21.4): an operator + customer either convert the pre-order to a
/// standalone order (→ Completed) or cancel it (→ FailedFinal); it never auto-advances.
/// <see cref="PreorderSagaState.HoldingCapacity"/> / <see cref="PreorderSagaState.AuthorizingPayment"/>
/// / <see cref="PreorderSagaState.CompensatingHold"/> are retained enum members that are no longer
/// wired into the forward or compensation paths (booking delegated to 06).</para>
/// </summary>
public static class PreorderSagaStateMachine
{
    private static readonly FrozenSet<PreorderSagaState> _inFlightForwardSteps = new[]
    {
        PreorderSagaState.ReservingItems,
        PreorderSagaState.CommittingBooking,
        PreorderSagaState.CommittingPreorder,
    }.ToFrozenSet();

    /// <summary>
    /// Every legal directed edge keyed by source state. States absent from this dictionary have no
    /// outgoing edges: the terminals (<see cref="PreorderSagaState.Completed"/>,
    /// <see cref="PreorderSagaState.FailedFinal"/>, <see cref="PreorderSagaState.ManualReview"/>) and
    /// the now-unwired legacy steps (<see cref="PreorderSagaState.HoldingCapacity"/>,
    /// <see cref="PreorderSagaState.AuthorizingPayment"/>, <see cref="PreorderSagaState.CompensatingHold"/>).
    /// </summary>
    public static readonly IReadOnlyDictionary<PreorderSagaState, IReadOnlySet<PreorderSagaState>> AllowedTransitions =
        new Dictionary<PreorderSagaState, IReadOnlySet<PreorderSagaState>>
        {
            [PreorderSagaState.Revalidating] = new HashSet<PreorderSagaState>
            {
                PreorderSagaState.ReservingItems, // items valid → reserve (items-first for all)
                PreorderSagaState.FailedFinal,    // stale item / price changed — nothing acquired
            }.ToFrozenSet(),
            [PreorderSagaState.ReservingItems] = new HashSet<PreorderSagaState>
            {
                PreorderSagaState.CommittingBooking,  // items reserved → trigger the 06 operation
                PreorderSagaState.CompensatingItems,  // reserve failed → release any partial items
                PreorderSagaState.Reconciling,
            }.ToFrozenSet(),
            [PreorderSagaState.CommittingBooking] = new HashSet<PreorderSagaState>
            {
                PreorderSagaState.CommittingPreorder, // 06 signalled Booked
                PreorderSagaState.CompensatingItems,  // 06 signalled failure → release items
                PreorderSagaState.Reconciling,
            }.ToFrozenSet(),
            [PreorderSagaState.CommittingPreorder] = new HashSet<PreorderSagaState>
            {
                PreorderSagaState.Completed,
                PreorderSagaState.CompensatingBooking, // 06 booking committed; preorder persist failed
                PreorderSagaState.Reconciling,
            }.ToFrozenSet(),
            [PreorderSagaState.CompensatingItems] = new HashSet<PreorderSagaState>
            {
                PreorderSagaState.FailedFinal, // items released — nothing left active
            }.ToFrozenSet(),
            [PreorderSagaState.CompensatingBooking] = new HashSet<PreorderSagaState>
            {
                // 06 booking committed but the pre-order could not be bound: trigger 06's
                // cancellation and route to operator + customer follow-up (§5.3). A pre-order never
                // lingers after a failed/cancelled booking.
                PreorderSagaState.ManualReview,
            }.ToFrozenSet(),
            [PreorderSagaState.Reconciling] = new HashSet<PreorderSagaState>
            {
                // The crash-recovery hub: re-check saga/operation state and resume the current
                // forward/compensation step, or escalate to a terminal when it cannot be confirmed.
                PreorderSagaState.ReservingItems,
                PreorderSagaState.CommittingBooking,
                PreorderSagaState.CommittingPreorder,
                PreorderSagaState.CompensatingItems,
                PreorderSagaState.CompensatingBooking,
                PreorderSagaState.Completed,
                PreorderSagaState.FailedFinal,
                PreorderSagaState.ManualReview,
            }.ToFrozenSet(),
            [PreorderSagaState.ManualReview] = new HashSet<PreorderSagaState>
            {
                // §21.4 operator resolution ONLY (not an automatic edge): an operator + the customer
                // either convert the parked pre-order to a standalone order (→ Completed, marked
                // standalone) or cancel it (→ FailedFinal). A pre-order never lingers active — it stays
                // in ManualReview until a human explicitly resolves it. Guarded in the resolution
                // handlers (operator actor + customer-agreement evidence required, audited).
                PreorderSagaState.Completed,
                PreorderSagaState.FailedFinal,
            }.ToFrozenSet(),
            // Terminal states — no outgoing edges; absent from the dictionary.
            // Completed, FailedFinal (ManualReview is resolvable by an operator, above)
            // Unwired legacy steps (booking delegated to 06) — also absent, no outgoing edges:
            // HoldingCapacity, AuthorizingPayment, CompensatingHold
        }.ToFrozenDictionary();

    /// <summary>Returns <c>true</c> if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(PreorderSagaState from, PreorderSagaState to)
        => AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>
    /// Validates that <paramref name="to"/> is a legal successor of <paramref name="from"/>.
    /// Throws <see cref="InvalidPreorderSagaTransitionException"/> if the edge is not in the §5.3 edge set.
    /// </summary>
    public static void EnsureAllowed(PreorderSagaState from, PreorderSagaState to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidPreorderSagaTransitionException(from, to);
    }

    /// <summary>
    /// True when <paramref name="state"/> is an in-flight forward step (a step that has acquired or
    /// is acquiring an external resource) — i.e. one a step-timeout can route to
    /// <see cref="PreorderSagaState.Reconciling"/>.
    /// </summary>
    public static bool IsInFlightForwardStep(PreorderSagaState state)
        => _inFlightForwardSteps.Contains(state);
}
