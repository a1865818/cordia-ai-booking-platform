using System.Collections.Frozen;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Domain.Entities;

/// <summary>
/// Encodes the §11 allowed-edge set and terminal states for the booking operation state machine
/// (spec 06 §6.1, §11). This is the single authoritative source of legal transitions in C#.
/// </summary>
public static class OperationStateMachine
{
    /// <summary>
    /// Every legal directed edge in the §11 state machine, keyed by source state.
    /// States absent from this dictionary have no outgoing edges (terminals).
    /// Backed by <see cref="FrozenDictionary"/> / <see cref="FrozenSet"/> for true immutability
    /// (C6 — not downcastable to mutable collections).
    /// </summary>
    public static readonly IReadOnlyDictionary<OperationStatus, IReadOnlySet<OperationStatus>> AllowedTransitions =
        new Dictionary<OperationStatus, IReadOnlySet<OperationStatus>>
        {
            [OperationStatus.Accepted] = new HashSet<OperationStatus>
            {
                OperationStatus.Validating,
                // Spec 11 §5.3 / Inv-10: a pre-order operation's live revalidation runs BEFORE the
                // 06 engine starts (the saga is the producer gate). Stale items park the op here
                // directly — nothing was acquired — so the customer is prompted to re-confirm.
                OperationStatus.AlternativesRequired,
                // Spec 11 §5.3 / §8 FM#8: a pre-order that fails at item-reserve BEFORE the 06
                // engine is ever triggered has no engine-owned path out of Accepted — the saga's
                // compensation terminalizes the operation in lockstep with the failure outcome so
                // the row never contradicts the terminal notification the customer received.
                OperationStatus.FailedFinal,
            }.ToFrozenSet(),
            [OperationStatus.Validating] = new HashSet<OperationStatus>
            {
                OperationStatus.AwaitingUserAction,
                OperationStatus.ReservingCapacity,
                // Spec 08 §5.3: a cancellation is its own operation and reserves nothing — it
                // undoes an existing booking. It therefore skips ReservingCapacity/Held and goes
                // straight to Finalizing, which is the only state with an edge to the Booked
                // terminal. Create/modify never take this edge (ValidateOperationCommandHandler
                // routes only OperationTypeEnum.Cancel here).
                OperationStatus.Finalizing,
                OperationStatus.Rejected,
            }.ToFrozenSet(),
            [OperationStatus.AwaitingUserAction] = new HashSet<OperationStatus>
            {
                OperationStatus.Accepted,
            }.ToFrozenSet(),
            [OperationStatus.ReservingCapacity] = new HashSet<OperationStatus>
            {
                OperationStatus.Held,
                OperationStatus.AlternativesRequired,
                OperationStatus.Reconciling,
            }.ToFrozenSet(),
            [OperationStatus.Held] = new HashSet<OperationStatus>
            {
                OperationStatus.AwaitingPayment,
                OperationStatus.Finalizing,
            }.ToFrozenSet(),
            [OperationStatus.AwaitingPayment] = new HashSet<OperationStatus>
            {
                OperationStatus.Finalizing,
                OperationStatus.Expired,
            }.ToFrozenSet(),
            [OperationStatus.Finalizing] = new HashSet<OperationStatus>
            {
                OperationStatus.Booked,
                OperationStatus.Reconciling,
                OperationStatus.CompensationRequired,
            }.ToFrozenSet(),
            [OperationStatus.Reconciling] = new HashSet<OperationStatus>
            {
                OperationStatus.Held,
                OperationStatus.Booked,
                OperationStatus.AlternativesRequired,
                OperationStatus.ManualReview,
            }.ToFrozenSet(),
            [OperationStatus.CompensationRequired] = new HashSet<OperationStatus>
            {
                OperationStatus.FailedFinal,
                OperationStatus.ManualReview,
            }.ToFrozenSet(),
            [OperationStatus.AlternativesRequired] = new HashSet<OperationStatus>
            {
                OperationStatus.Waitlisted,
                OperationStatus.FailedFinal,
            }.ToFrozenSet(),
            [OperationStatus.ManualReview] = new HashSet<OperationStatus>
            {
                OperationStatus.Reconciling,
                OperationStatus.Booked,
                OperationStatus.FailedFinal,
            }.ToFrozenSet(),
            // Terminal states — no outgoing edges; absent from the dictionary.
            // Booked, Rejected, Expired, Waitlisted, FailedFinal
        }.ToFrozenDictionary();

    private static readonly IReadOnlySet<OperationStatus> _terminalStates =
        new HashSet<OperationStatus>
        {
            OperationStatus.Booked,
            OperationStatus.Rejected,
            OperationStatus.Expired,
            OperationStatus.Waitlisted,
            OperationStatus.FailedFinal,
        }.ToFrozenSet();

    /// <summary>Returns <c>true</c> if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(OperationStatus from, OperationStatus to)
        => AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Returns <c>true</c> if <paramref name="status"/> is a terminal state (no outgoing edges).</summary>
    public static bool IsTerminal(OperationStatus status)
        => _terminalStates.Contains(status);

    /// <summary>
    /// Validates that <paramref name="to"/> is a legal successor of <paramref name="from"/>.
    /// Throws <see cref="InvalidStateTransitionException"/> if the edge is not in the §11 edge set.
    /// </summary>
    public static void EnsureAllowed(OperationStatus from, OperationStatus to)
    {
        if (!IsAllowed(from, to))
            throw new InvalidStateTransitionException(from, to);
    }
}
