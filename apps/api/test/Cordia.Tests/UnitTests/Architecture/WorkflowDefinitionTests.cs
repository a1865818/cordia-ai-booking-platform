using Cordia.Application.Handlers.BookingFulfillment.AdvanceOperation;
using Cordia.Application.Handlers.Console.ListOperations;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Preorder;
using Cordia.Domain.Waitlist;
using Cordia.Infrastructure.Services.Retention;

namespace Cordia.Tests.UnitTests.Architecture;

/// <summary>
/// The guarantee that <b>a workflow change cannot silently make dependent behaviour incorrect</b>.
///
/// <para>Every other test in this suite asks "does this handler do the right thing?". These ask a
/// different question: "if someone edits the state machine, will anything tell them what else has to
/// move?" Historically the answer was no. A reconciler kept its own terminal set, the set fell
/// behind the enum, and two states were re-probed to exhaustion and then abandoned — with nothing
/// failing anywhere. The fix for that instance was to delete the copy; the fix for the CLASS is
/// here, because the next copy will be added by someone who does not know the story.</para>
///
/// <para>These tests are therefore deliberately about RELATIONSHIPS between definitions, not about
/// their contents. None of them asserts that a particular status is terminal — that is the state
/// machine's business, and pinning it here would just be a sixteenth copy.</para>
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowDefinitionTests
{
    private static readonly OperationStatus[] AllStatuses = Enum.GetValues<OperationStatus>();

    /// <summary>
    /// D1 is frozen at 16 values. If this fails, a status was added or removed and every
    /// classification below needs a decision — which is exactly what the rest of this class forces.
    /// </summary>
    [Fact]
    public void OperationStatus_HasExactlyTheSixteenRatifiedValues()
        => Assert.Equal(16, AllStatuses.Length);

    /// <summary>
    /// Terminality has ONE definition: no outgoing edges. The class used to carry the edge map AND a
    /// hand-written list of terminals, so removing a state's last edge left it non-terminal until
    /// someone remembered to edit both.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void IsTerminal_IsExactlyAbsenceFromTheEdgeMap(OperationStatus status)
        => Assert.Equal(
            !OperationStateMachine.AllowedTransitions.ContainsKey(status),
            OperationStateMachine.IsTerminal(status));

    /// <summary>
    /// Every status is classified exactly once: ended, waiting on the customer, waiting on an
    /// operator, or being driven by the platform.
    ///
    /// <para>This is the test that makes adding a status a decision rather than a default. A new
    /// member lands in "driver dispatchable" by construction, and the next test then demands the
    /// driver actually have a step for it — so the pair fail together and say what is missing.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void EveryStatus_IsClassifiedExactlyOnce(OperationStatus status)
    {
        var classifications = new[]
        {
            OperationStateMachine.IsTerminal(status),
            OperationStateMachine.IsAwaitingUser(status),
            OperationStateMachine.RequiresOperator(status),
            OperationStateMachine.IsDriverDispatchable(status),
        };

        Assert.Equal(1, classifications.Count(c => c));
    }

    /// <summary>
    /// <b>The driver's dispatch table and the state machine agree.</b>
    ///
    /// <para>The driver decides what to do next with a <c>switch</c> whose default arm returns null —
    /// "wait". That fails SAFE but SILENTLY: add a status and operations in it park forever, with no
    /// exception, no log and no test. Here the switch is enumerated against
    /// <see cref="OperationStateMachine.IsDriverDispatchable"/>, so a new state either gets a driver
    /// step or is explicitly classified as a wait state — and forgetting to do either fails.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void DriverDispatch_MatchesTheStateMachinesClassification(OperationStatus status)
        => Assert.Equal(
            OperationStateMachine.IsDriverDispatchable(status),
            AdvanceBookingOperationCommandHandler.HasDriverStep(status));

    /// <summary>
    /// The SLA partition is a partition: eligible states and user-wait states are disjoint, and
    /// together with the terminals they cover all 16.
    ///
    /// <para>These two halves lived in different projects and were maintained independently — one in
    /// the repository's SQL filter, one in the SLA handler — so a status could have been in both or
    /// in neither with nothing to notice.</para>
    /// </summary>
    [Fact]
    public void SlaPartition_IsDisjointAndTotal()
    {
        Assert.Empty(OperationStateMachine.SlaEligibleStates
            .Intersect(OperationStateMachine.UserWaitStates));

        var covered = OperationStateMachine.SlaEligibleStates
            .Concat(OperationStateMachine.UserWaitStates)
            .Concat(OperationStateMachine.TerminalStates)
            .Distinct()
            .ToList();

        Assert.Equal(AllStatuses.Length, covered.Count);
    }

    /// <summary>
    /// A terminal failure is a terminal that is not a success and not merely informational. Derived,
    /// so a new terminal failure reaches the funnel report, the missing-notification sweep and the
    /// booking lifecycle projection at once instead of one of the three.
    /// </summary>
    [Fact]
    public void TerminalFailures_AreTerminalsMinusBookedAndWaitlisted()
    {
        Assert.All(OperationStateMachine.TerminalFailureStates,
            s => Assert.True(OperationStateMachine.IsTerminal(s)));

        Assert.DoesNotContain(OperationStatus.Booked, OperationStateMachine.TerminalFailureStates);
        Assert.DoesNotContain(OperationStatus.Waitlisted, OperationStateMachine.TerminalFailureStates);
        Assert.Equal(
            OperationStateMachine.TerminalStates.Count - 2,
            OperationStateMachine.TerminalFailureStates.Count);
    }

    /// <summary>
    /// The operator queue classifies every status deliberately — on the board, or consciously off it.
    ///
    /// <para><c>NeedsAttention</c> is a product judgement, not a fact the state machine owns, so it
    /// stays a list. What it is not allowed to be is silently incomplete: a new status that nobody
    /// decides about would simply never appear on the board an operator works from.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryStatus))]
    public void NeedsAttention_ClassifiesEveryStatusDeliberately(OperationStatus status)
    {
        var onBoard = ListOperationsQueryHandler.NeedsAttention.Contains(status);
        var deliberatelyOff = ListOperationsQueryHandler.DeliberatelyNotSurfaced.Contains(status);

        Assert.True(
            onBoard ^ deliberatelyOff,
            $"{status} is on neither list (or both). Decide whether an operator needs to see it.");
    }

    /// <summary>
    /// Retention's "prunable" is deliberately NOT the workflow's "terminal", and the difference is
    /// exactly one documented state.
    ///
    /// <para>Pinned because the divergence looks like a bug: a reader who assumes the two mean the
    /// same thing would "fix" it by adding <c>Waitlisted</c> and start deleting the records of guests
    /// still sitting on a live waitlist. This test states the intent so the next person finds an
    /// argument instead of an inconsistency.</para>
    /// </summary>
    [Fact]
    public void RetentionPrunable_IsTerminalMinusWaitlistedOnly()
    {
        Assert.False(RetentionPolicy.IsPrunable(OperationStatus.Waitlisted));
        Assert.True(OperationStateMachine.IsTerminal(OperationStatus.Waitlisted));

        Assert.Equal(
            OperationStateMachine.TerminalStates.Where(s => s != OperationStatus.Waitlisted).OrderBy(s => s),
            RetentionPolicy.PrunableStatuses.OrderBy(s => s));
    }

    // ── The other four state machines ────────────────────────────────────────────────────────

    /// <summary>
    /// Every machine that callers interrogate exposes <c>IsTerminal</c>, and its terminals genuinely
    /// have no outgoing edges.
    ///
    /// <para>Four of the five did not expose this, which is WHY four call sites had hand-rolled
    /// terminal sets: not carelessness, a missing authority. Deriving is safe for these three
    /// because every absent member is a real ending.</para>
    /// </summary>
    [Fact]
    public void SecondaryStateMachines_DeriveTerminalsFromTheirEdgeMaps()
    {
        AssertDerived(
            Enum.GetValues<NotificationState>(),
            NotificationStateMachine.AllowedTransitions.ContainsKey,
            NotificationStateMachine.IsTerminal);

        AssertDerived(
            Enum.GetValues<WaitlistOfferStatus>(),
            WaitlistOfferStateMachine.AllowedTransitions.ContainsKey,
            WaitlistOfferStateMachine.IsTerminal);

        AssertDerived(
            Enum.GetValues<CatalogStatus>(),
            CatalogStateMachine.AllowedTransitions.ContainsKey,
            CatalogStateMachine.IsTerminal);

        static void AssertDerived<T>(T[] all, Func<T, bool> hasEdges, Func<T, bool> isTerminal)
        {
            foreach (var value in all)
                Assert.Equal(!hasEdges(value), isTerminal(value));
        }
    }

    /// <summary>
    /// The pre-order saga LISTS its terminals rather than deriving them, and that is correct —
    /// but the honest half of the derivation still has to hold.
    ///
    /// <para>It carries unwired legacy members (<c>HoldingCapacity</c>, <c>AuthorizingPayment</c>,
    /// <c>CompensatingHold</c>) that have no outgoing edges yet are dead states, not endings.
    /// Deriving would call them terminal and tell a reconciler a stuck saga had finished. So the
    /// assertion runs one way only: everything LISTED as terminal must really have no way out.</para>
    /// </summary>
    [Fact]
    public void PreorderSagaTerminals_HaveNoOutgoingEdges()
    {
        Assert.All(PreorderSagaStateMachine.TerminalStates,
            s => Assert.False(PreorderSagaStateMachine.AllowedTransitions.ContainsKey(s)));

        // ManualReview is NOT terminal — it has two operator-driven edges out (§21.4) — but a
        // reconciler must still stop at it, which is what the separate query is for.
        Assert.False(PreorderSagaStateMachine.IsTerminal(PreorderSagaState.ManualReview));
        Assert.True(PreorderSagaStateMachine.IsResolvedForReconcile(PreorderSagaState.ManualReview));
    }

    public static TheoryData<OperationStatus> EveryStatus()
    {
        var data = new TheoryData<OperationStatus>();
        foreach (var status in AllStatuses)
            data.Add(status);
        return data;
    }
}
