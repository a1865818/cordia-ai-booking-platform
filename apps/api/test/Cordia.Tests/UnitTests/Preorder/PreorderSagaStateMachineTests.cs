using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Preorder;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Unit tests for <see cref="PreorderSagaStateMachine"/> — the authoritative forward + compensation
/// edge set for the spec 11 §5.3 pre-order saga, with the booking delegated to the spec-06 operation
/// engine. Forward: Revalidating → ReservingItems → CommittingBooking (await 06) → CommittingPreorder
/// → Completed. Compensation unwinds only the saga's reserved items (CompensatingItems → FailedFinal)
/// or, once 06 has committed the booking, escalates via CompensatingBooking → ManualReview; any
/// in-flight forward step can time out to Reconciling; terminals are Completed / FailedFinal, while
/// ManualReview is an operator-resolution park (§21.4 → Completed / FailedFinal). The legacy
/// HoldingCapacity / AuthorizingPayment / CompensatingHold steps are retained
/// enum members but no longer wired (07/09 owned by 06).
/// </summary>
[Trait("Category", "Unit")]
public class PreorderSagaStateMachineTests
{
    public static readonly TheoryData<PreorderSagaState, PreorderSagaState> AllowedEdges = new()
    {
        // Forward happy path (booking delegated to 06).
        { PreorderSagaState.Revalidating, PreorderSagaState.ReservingItems },
        { PreorderSagaState.ReservingItems, PreorderSagaState.CommittingBooking },
        { PreorderSagaState.CommittingBooking, PreorderSagaState.CommittingPreorder },
        { PreorderSagaState.CommittingPreorder, PreorderSagaState.Completed },
        // Early failure with nothing acquired → straight to terminal.
        { PreorderSagaState.Revalidating, PreorderSagaState.FailedFinal },
        // Compensation edges (only the saga's reserved items; 06 owns booking/payment/hold unwind).
        { PreorderSagaState.ReservingItems, PreorderSagaState.CompensatingItems },   // reserve failed
        { PreorderSagaState.CommittingBooking, PreorderSagaState.CompensatingItems },// 06 booking failed
        { PreorderSagaState.CommittingPreorder, PreorderSagaState.CompensatingBooking }, // preorder persist failed
        { PreorderSagaState.CompensatingItems, PreorderSagaState.FailedFinal },
        { PreorderSagaState.CompensatingBooking, PreorderSagaState.ManualReview },
        // §21.4 operator resolution of a ManualReview park: convert to standalone or cancel.
        { PreorderSagaState.ManualReview, PreorderSagaState.Completed },
        { PreorderSagaState.ManualReview, PreorderSagaState.FailedFinal },
        // Timeout → reconcile from any in-flight forward step.
        { PreorderSagaState.ReservingItems, PreorderSagaState.Reconciling },
        { PreorderSagaState.CommittingBooking, PreorderSagaState.Reconciling },
        { PreorderSagaState.CommittingPreorder, PreorderSagaState.Reconciling },
        // Reconcile resumes or escalates.
        { PreorderSagaState.Reconciling, PreorderSagaState.CommittingBooking },
        { PreorderSagaState.Reconciling, PreorderSagaState.CommittingPreorder },
        { PreorderSagaState.Reconciling, PreorderSagaState.CompensatingItems },
        { PreorderSagaState.Reconciling, PreorderSagaState.ManualReview },
        { PreorderSagaState.Reconciling, PreorderSagaState.FailedFinal },
    };

    public static readonly TheoryData<PreorderSagaState, PreorderSagaState> DisallowedEdges = new()
    {
        { PreorderSagaState.Revalidating, PreorderSagaState.Completed },            // can't skip the saga
        { PreorderSagaState.Revalidating, PreorderSagaState.CommittingBooking },    // can't skip item reserve
        { PreorderSagaState.ReservingItems, PreorderSagaState.CommittingPreorder }, // can't skip the booking
        { PreorderSagaState.Completed, PreorderSagaState.Revalidating },            // terminal
        { PreorderSagaState.FailedFinal, PreorderSagaState.ReservingItems },        // terminal
        { PreorderSagaState.ManualReview, PreorderSagaState.ReservingItems },       // no resume from a human park
        { PreorderSagaState.CommittingPreorder, PreorderSagaState.ReservingItems }, // no backward edge
        // Legacy saga-level booking steps are unwired (delegated to 06): no outgoing edges.
        { PreorderSagaState.Revalidating, PreorderSagaState.HoldingCapacity },      // saga no longer holds capacity
        { PreorderSagaState.ReservingItems, PreorderSagaState.AuthorizingPayment }, // saga no longer authorizes payment
        { PreorderSagaState.HoldingCapacity, PreorderSagaState.ReservingItems },    // unwired legacy step
        { PreorderSagaState.AuthorizingPayment, PreorderSagaState.CommittingBooking }, // unwired legacy step
    };

    // ManualReview is NOT terminal — it is an operator-resolution park (§21.4) with edges to
    // Completed (convert to standalone) / FailedFinal (cancel). Only these two are truly terminal.
    public static readonly TheoryData<PreorderSagaState> TerminalStates = new()
    {
        PreorderSagaState.Completed,
        PreorderSagaState.FailedFinal,
    };

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void IsAllowed_ReturnsTrue_ForLegalEdges(PreorderSagaState from, PreorderSagaState to)
        => Assert.True(PreorderSagaStateMachine.IsAllowed(from, to));

    [Theory]
    [MemberData(nameof(DisallowedEdges))]
    public void IsAllowed_ReturnsFalse_ForIllegalEdges(PreorderSagaState from, PreorderSagaState to)
        => Assert.False(PreorderSagaStateMachine.IsAllowed(from, to));

    [Theory]
    [MemberData(nameof(DisallowedEdges))]
    public void EnsureAllowed_Throws_ForIllegalEdges(PreorderSagaState from, PreorderSagaState to)
    {
        var ex = Assert.Throws<InvalidPreorderSagaTransitionException>(
            () => PreorderSagaStateMachine.EnsureAllowed(from, to));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
    }

    [Theory]
    [MemberData(nameof(TerminalStates))]
    public void TerminalStates_HaveNoOutgoingEdges(PreorderSagaState terminal)
    {
        foreach (PreorderSagaState to in Enum.GetValues<PreorderSagaState>())
            Assert.False(PreorderSagaStateMachine.IsAllowed(terminal, to));
    }

    [Fact]
    public void EnsureAllowed_DoesNotThrow_ForForwardHappyPath()
    {
        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.Revalidating, PreorderSagaState.ReservingItems);
        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.ReservingItems, PreorderSagaState.CommittingBooking);
        PreorderSagaStateMachine.EnsureAllowed(PreorderSagaState.CommittingPreorder, PreorderSagaState.Completed);
    }

    [Theory]
    [InlineData(PreorderSagaState.ReservingItems)]
    [InlineData(PreorderSagaState.CommittingBooking)]
    [InlineData(PreorderSagaState.CommittingPreorder)]
    public void IsInFlightForwardStep_ReturnsTrue_ForNewForwardSteps(PreorderSagaState state)
        => Assert.True(PreorderSagaStateMachine.IsInFlightForwardStep(state));

    [Theory]
    [InlineData(PreorderSagaState.HoldingCapacity)]   // delegated to 06 — no longer a saga step
    [InlineData(PreorderSagaState.AuthorizingPayment)] // delegated to 06 — no longer a saga step
    [InlineData(PreorderSagaState.Revalidating)]       // pre-flight, holds nothing external
    [InlineData(PreorderSagaState.Completed)]
    public void IsInFlightForwardStep_ReturnsFalse_ForNonForwardSteps(PreorderSagaState state)
        => Assert.False(PreorderSagaStateMachine.IsInFlightForwardStep(state));
}
