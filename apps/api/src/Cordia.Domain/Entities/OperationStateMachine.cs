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

    /// <summary>
    /// The terminal states — <b>derived</b> from <see cref="AllowedTransitions"/>, never listed.
    ///
    /// <para>"Terminal" already has a definition one line up: a state with no outgoing edges. Writing
    /// the members out again created a second source of the same fact inside the authority itself,
    /// and a state whose last edge was removed would have stayed non-terminal until someone
    /// remembered to edit both. Deriving makes that impossible.</para>
    ///
    /// <para>Exposed because callers that cannot invoke <see cref="IsTerminal"/> — EF Core cannot
    /// translate a method call into SQL — must still be able to MATERIALISE this set rather than
    /// retype it. See <c>BookingOperationRepository.TerminalStatuses</c>.</para>
    ///
    /// <para>Safe to derive here specifically because every <see cref="OperationStatus"/> absent from
    /// the edge map is genuinely an ending. That is <b>not</b> true of every state machine in this
    /// codebase — <c>PreorderSagaStateMachine</c> carries unwired legacy members that are also absent
    /// yet are not terminals — so that one lists its terminals explicitly and pins the relationship
    /// with a test instead.</para>
    /// </summary>
    public static readonly IReadOnlySet<OperationStatus> TerminalStates =
        Enum.GetValues<OperationStatus>()
            .Where(s => !AllowedTransitions.ContainsKey(s))
            .ToFrozenSet();

    /// <summary>
    /// States that are waiting on a PERSON OUTSIDE the platform — the customer — rather than on the
    /// platform itself. Not terminal: they resume when that person acts, or expire.
    ///
    /// <para>Owned here because two files previously encoded this partition from opposite
    /// directions: the SLA sweeper's "eligible" list and its "user wait" list were maintained
    /// independently, so a new state could have landed in both or in neither. Now
    /// <see cref="IsSlaEligible"/> is defined as the complement and the two cannot disagree.</para>
    /// </summary>
    public static readonly IReadOnlySet<OperationStatus> UserWaitStates =
        new HashSet<OperationStatus>
        {
            OperationStatus.AwaitingUserAction,
            OperationStatus.AwaitingPayment,
            OperationStatus.AlternativesRequired,
        }.ToFrozenSet();

    /// <summary>
    /// States a HUMAN OPERATOR owns: the platform will not advance them on its own, and no customer
    /// action will either. They exit only through the typed recovery commands (Inv-15).
    /// </summary>
    public static readonly IReadOnlySet<OperationStatus> OperatorOwnedStates =
        new HashSet<OperationStatus>
        {
            OperationStatus.ManualReview,
        }.ToFrozenSet();

    /// <summary>
    /// States the SLA sweeper evaluates — <b>derived</b> as "not finished, and not the customer's
    /// turn". Everything left is time the PLATFORM is accountable for.
    ///
    /// <para>Includes <see cref="OperationStatus.ManualReview"/> deliberately: an operator sitting on
    /// a review past its deadline is exactly the kind of delay an SLA is meant to surface. Being
    /// operator-owned stops the DRIVER from dispatching it; it does not stop the CLOCK.</para>
    ///
    /// <para>Materialised as a set because <c>BookingOperationRepository</c> needs it as an array —
    /// EF Core cannot translate a predicate method into SQL — and retyping it there is what let the
    /// two halves of this partition drift apart in the first place.</para>
    /// </summary>
    public static readonly IReadOnlySet<OperationStatus> SlaEligibleStates =
        Enum.GetValues<OperationStatus>()
            .Where(s => !TerminalStates.Contains(s) && !UserWaitStates.Contains(s))
            .ToFrozenSet();

    /// <summary>Returns <c>true</c> when the SLA clock applies to <paramref name="status"/>.</summary>
    public static bool IsSlaEligible(OperationStatus status)
        => SlaEligibleStates.Contains(status);

    /// <summary>
    /// Terminal states that are a customer-visible FAILURE — <b>derived</b> as the terminals minus
    /// the two that are not failures.
    ///
    /// <para><see cref="OperationStatus.Booked"/> is the success outcome.
    /// <see cref="OperationStatus.Waitlisted"/> is informational: the request ended, but the guest
    /// is on a list and may still be seated, so reporting it as a failure would be wrong and
    /// notifying it as one would be worse (there is no <c>waitlisted.v1</c> failure template by
    /// design).</para>
    ///
    /// <para>Derived rather than listed so a NEW terminal failure state is a failure everywhere at
    /// once. Three independent copies of this set previously existed — the funnel read model, the
    /// notification sweep's constants, and that same sweep's inline LINQ predicate — and a new
    /// terminal would have been silently missing from all three.</para>
    /// </summary>
    public static readonly IReadOnlySet<OperationStatus> TerminalFailureStates =
        TerminalStates
            .Where(s => s is not OperationStatus.Booked and not OperationStatus.Waitlisted)
            .ToFrozenSet();

    /// <summary>Returns <c>true</c> when <paramref name="status"/> is a customer-visible failure ending.</summary>
    public static bool IsTerminalFailure(OperationStatus status)
        => TerminalFailureStates.Contains(status);

    /// <summary>Returns <c>true</c> if <paramref name="to"/> is a legal successor of <paramref name="from"/>.</summary>
    public static bool IsAllowed(OperationStatus from, OperationStatus to)
        => AllowedTransitions.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Returns <c>true</c> if <paramref name="status"/> is a terminal state (no outgoing edges).</summary>
    public static bool IsTerminal(OperationStatus status)
        => TerminalStates.Contains(status);

    /// <summary>Returns <c>true</c> when the operation is waiting on the CUSTOMER.</summary>
    public static bool IsAwaitingUser(OperationStatus status)
        => UserWaitStates.Contains(status);

    /// <summary>Returns <c>true</c> when the operation is waiting on a human OPERATOR (Inv-15).</summary>
    public static bool RequiresOperator(OperationStatus status)
        => OperatorOwnedStates.Contains(status);

    /// <summary>
    /// Returns <c>true</c> when the fulfilment driver has a step for this state — i.e. the platform
    /// itself will advance it without any human.
    ///
    /// <para><b>Derived, not listed.</b> A state is driver-dispatchable exactly when it is neither
    /// terminal, nor waiting on the customer, nor owned by an operator. The driver's <c>switch</c>
    /// previously encoded this by omission (<c>_ =&gt; null</c>), which fails safe but silently: a new
    /// state parks forever and nothing says so. <c>WorkflowDefinitionTests</c> asserts the switch and
    /// this predicate agree exactly, so adding a state without a driver step now fails a test
    /// instead of stalling an operation in production.</para>
    /// </summary>
    public static bool IsDriverDispatchable(OperationStatus status)
        => !IsTerminal(status) && !IsAwaitingUser(status) && !RequiresOperator(status);

    /// <summary>
    /// The legal successors of <paramref name="status"/> — "what may happen next" asked of the
    /// definition rather than restated by the caller. Empty for a terminal state.
    /// </summary>
    public static IReadOnlySet<OperationStatus> NextStates(OperationStatus status)
        => AllowedTransitions.TryGetValue(status, out var targets)
            ? targets
            : FrozenSet<OperationStatus>.Empty;

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
