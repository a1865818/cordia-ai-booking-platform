using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// Exhaustive unit tests for <see cref="OperationStateMachine"/> (spec 06 §11, §6.1).
/// Covers: every allowed edge, a representative set of forbidden edges, terminal states,
/// and <see cref="BookingOperation.TransitionTo"/> side effects.
/// </summary>
[Trait("Category", "Unit")]
public class OperationStateMachineTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static BookingOperation MakeOperation(OperationStatus status) =>
        new() { Status = status, UpdatedAt = DateTimeOffset.MinValue };

    // -----------------------------------------------------------------------
    // IsAllowed / EnsureAllowed — exhaustive over all (from, to) pairs
    // -----------------------------------------------------------------------

    /// <summary>
    /// For every ordered pair of OperationStatus values, IsAllowed must return exactly
    /// whether (from, to) appears in AllowedTransitions, and EnsureAllowed must either
    /// succeed or throw InvalidStateTransitionException accordingly.
    /// </summary>
    [Fact]
    public void AllStatusPairs_IsAllowed_MatchesAllowedTransitions()
    {
        var allStatuses = Enum.GetValues<OperationStatus>();

        foreach (var from in allStatuses)
        {
            foreach (var to in allStatuses)
            {
                bool inDictionary =
                    OperationStateMachine.AllowedTransitions.TryGetValue(from, out var targets)
                    && targets.Contains(to);

                bool isAllowed = OperationStateMachine.IsAllowed(from, to);
                Assert.Equal(inDictionary, isAllowed);

                if (isAllowed)
                {
                    // Must NOT throw
                    OperationStateMachine.EnsureAllowed(from, to);
                }
                else
                {
                    var ex = Assert.Throws<InvalidStateTransitionException>(
                        () => OperationStateMachine.EnsureAllowed(from, to));
                    Assert.Equal(from, ex.From);
                    Assert.Equal(to, ex.To);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Spot-check every allowed edge individually (readable test names in output)
    // -----------------------------------------------------------------------

    public static TheoryData<OperationStatus, OperationStatus> AllowedEdges()
    {
        var data = new TheoryData<OperationStatus, OperationStatus>();
        foreach (var (from, targets) in OperationStateMachine.AllowedTransitions)
            foreach (var to in targets)
                data.Add(from, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void IsAllowed_ReturnsTrue_ForAllowedEdge(OperationStatus from, OperationStatus to)
        => Assert.True(OperationStateMachine.IsAllowed(from, to));

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void EnsureAllowed_DoesNotThrow_ForAllowedEdge(OperationStatus from, OperationStatus to)
        => OperationStateMachine.EnsureAllowed(from, to); // must not throw

    // -----------------------------------------------------------------------
    // Representative FORBIDDEN edges
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(OperationStatus.Accepted, OperationStatus.Booked)]           // skip the whole graph
    [InlineData(OperationStatus.Validating, OperationStatus.Held)]           // jump over ReservingCapacity
    [InlineData(OperationStatus.Finalizing, OperationStatus.Held)]           // backward bypass
    [InlineData(OperationStatus.Held, OperationStatus.Booked)]               // skip Finalizing
    [InlineData(OperationStatus.Booked, OperationStatus.Accepted)]           // terminal → any
    [InlineData(OperationStatus.Booked, OperationStatus.Validating)]
    [InlineData(OperationStatus.Rejected, OperationStatus.Accepted)]         // terminal → any
    [InlineData(OperationStatus.Expired, OperationStatus.Held)]
    [InlineData(OperationStatus.Waitlisted, OperationStatus.Reconciling)]
    [InlineData(OperationStatus.FailedFinal, OperationStatus.ManualReview)]
    [InlineData(OperationStatus.Reconciling, OperationStatus.Finalizing)]    // no back-edge to Finalizing
    [InlineData(OperationStatus.ManualReview, OperationStatus.Accepted)]     // not a defined edge
    public void IsAllowed_ReturnsFalse_ForForbiddenEdge(OperationStatus from, OperationStatus to)
        => Assert.False(OperationStateMachine.IsAllowed(from, to));

    [Theory]
    [InlineData(OperationStatus.Accepted, OperationStatus.Booked)]
    [InlineData(OperationStatus.Validating, OperationStatus.Held)]
    [InlineData(OperationStatus.Finalizing, OperationStatus.Held)]
    [InlineData(OperationStatus.Held, OperationStatus.Booked)]
    [InlineData(OperationStatus.Booked, OperationStatus.Accepted)]
    [InlineData(OperationStatus.Rejected, OperationStatus.Accepted)]
    public void EnsureAllowed_Throws_ForForbiddenEdge(OperationStatus from, OperationStatus to)
    {
        var ex = Assert.Throws<InvalidStateTransitionException>(
            () => OperationStateMachine.EnsureAllowed(from, to));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
        Assert.Contains(from.ToString(), ex.Message);
        Assert.Contains(to.ToString(), ex.Message);
    }

    // -----------------------------------------------------------------------
    // IsTerminal
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(OperationStatus.Booked)]
    [InlineData(OperationStatus.Rejected)]
    [InlineData(OperationStatus.Expired)]
    [InlineData(OperationStatus.Waitlisted)]
    [InlineData(OperationStatus.FailedFinal)]
    public void IsTerminal_ReturnsTrue_ForTerminalStates(OperationStatus status)
        => Assert.True(OperationStateMachine.IsTerminal(status));

    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.Validating)]
    [InlineData(OperationStatus.AwaitingUserAction)]
    [InlineData(OperationStatus.ReservingCapacity)]
    [InlineData(OperationStatus.Held)]
    [InlineData(OperationStatus.AwaitingPayment)]
    [InlineData(OperationStatus.Finalizing)]
    [InlineData(OperationStatus.AlternativesRequired)]
    [InlineData(OperationStatus.Reconciling)]
    [InlineData(OperationStatus.CompensationRequired)]
    [InlineData(OperationStatus.ManualReview)]
    public void IsTerminal_ReturnsFalse_ForNonTerminalStates(OperationStatus status)
        => Assert.False(OperationStateMachine.IsTerminal(status));

    /// <summary>
    /// Terminal states must have NO outgoing edges in AllowedTransitions,
    /// and non-terminal non-source states must not reach terminals without a path.
    /// </summary>
    [Fact]
    public void TerminalStates_HaveNoOutgoingEdgesInAllowedTransitions()
    {
        var allStatuses = Enum.GetValues<OperationStatus>();
        foreach (var status in allStatuses)
        {
            bool isTerminal = OperationStateMachine.IsTerminal(status);
            bool hasOutgoing = OperationStateMachine.AllowedTransitions.ContainsKey(status);
            // A terminal state must NOT appear as a key (i.e. must have no outgoing edges).
            Assert.True(!isTerminal || !hasOutgoing,
                $"Terminal state {status} must have no outgoing edges in AllowedTransitions.");
        }
    }

    // -----------------------------------------------------------------------
    // BookingOperation.TransitionTo — side effects
    // -----------------------------------------------------------------------

    [Fact]
    public void TransitionTo_AllowedEdge_MutatesStatusAndReason()
    {
        var op = MakeOperation(OperationStatus.Accepted);

        op.TransitionTo(OperationStatus.Validating, reason: "starting validation");

        Assert.Equal(OperationStatus.Validating, op.Status);
        Assert.Equal("starting validation", op.StatusReason);
        Assert.NotEqual(DateTimeOffset.MinValue, op.UpdatedAt);
    }

    [Fact]
    public void TransitionTo_AllowedEdge_NullReason_SetsStatusReasonNull()
    {
        var op = MakeOperation(OperationStatus.Accepted);
        op.StatusReason = "previous reason";

        op.TransitionTo(OperationStatus.Validating);

        Assert.Equal(OperationStatus.Validating, op.Status);
        Assert.Null(op.StatusReason);
    }

    [Fact]
    public void TransitionTo_ForbiddenEdge_ThrowsAndDoesNotMutate()
    {
        var op = MakeOperation(OperationStatus.Accepted);
        op.StatusReason = "original";
        var originalUpdatedAt = op.UpdatedAt;

        var ex = Assert.Throws<InvalidStateTransitionException>(
            () => op.TransitionTo(OperationStatus.Booked));

        Assert.Equal(OperationStatus.Accepted, op.Status);    // unchanged
        Assert.Equal("original", op.StatusReason);            // unchanged
        Assert.Equal(originalUpdatedAt, op.UpdatedAt);        // unchanged
        Assert.Equal(OperationStatus.Accepted, ex.From);
        Assert.Equal(OperationStatus.Booked, ex.To);
    }

    [Fact]
    public void TransitionTo_SetsUpdatedAtToUtcNow()
    {
        var op = MakeOperation(OperationStatus.Held);
        var before = DateTimeOffset.UtcNow;

        op.TransitionTo(OperationStatus.Finalizing);

        Assert.True(op.UpdatedAt >= before);
        Assert.Equal(TimeSpan.Zero, op.UpdatedAt.Offset); // UTC
    }

    // -----------------------------------------------------------------------
    // AllowedTransitions covers exactly 11 non-terminal source states
    // -----------------------------------------------------------------------

    [Fact]
    public void AllowedTransitions_HasExactly11SourceStates()
    {
        // 16 states total − 5 terminal = 11 non-terminal sources
        Assert.Equal(11, OperationStateMachine.AllowedTransitions.Count);
    }
}
