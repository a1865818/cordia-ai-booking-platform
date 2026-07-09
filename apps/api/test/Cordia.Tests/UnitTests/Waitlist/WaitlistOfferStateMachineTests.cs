using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Waitlist;

namespace Cordia.Tests.UnitTests.Waitlist;

/// <summary>
/// Unit tests for <see cref="WaitlistOfferStateMachine"/> (spec 11 §4.4, §5.1). Asserts the exact
/// allowed-edge set and that every other edge throws <see cref="InvalidWaitlistOfferTransitionException"/>.
/// </summary>
[Trait("Category", "Unit")]
public class WaitlistOfferStateMachineTests
{
    public static TheoryData<WaitlistOfferStatus, WaitlistOfferStatus> AllowedEdges => new()
    {
        { WaitlistOfferStatus.Pending, WaitlistOfferStatus.Matched },
        { WaitlistOfferStatus.Matched, WaitlistOfferStatus.Offered },
        { WaitlistOfferStatus.Matched, WaitlistOfferStatus.Released },
        { WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted },
        { WaitlistOfferStatus.Offered, WaitlistOfferStatus.Declined },
        { WaitlistOfferStatus.Offered, WaitlistOfferStatus.Expired },
        { WaitlistOfferStatus.Offered, WaitlistOfferStatus.Released },
    };

    public static TheoryData<WaitlistOfferStatus, WaitlistOfferStatus> DisallowedEdges => new()
    {
        // Skipping states.
        { WaitlistOfferStatus.Pending, WaitlistOfferStatus.Offered },
        { WaitlistOfferStatus.Pending, WaitlistOfferStatus.Accepted },
        { WaitlistOfferStatus.Pending, WaitlistOfferStatus.Released },
        { WaitlistOfferStatus.Matched, WaitlistOfferStatus.Accepted },
        { WaitlistOfferStatus.Matched, WaitlistOfferStatus.Expired },
        // Backwards.
        { WaitlistOfferStatus.Offered, WaitlistOfferStatus.Matched },
        { WaitlistOfferStatus.Matched, WaitlistOfferStatus.Pending },
        // Out of terminals.
        { WaitlistOfferStatus.Accepted, WaitlistOfferStatus.Released },
        { WaitlistOfferStatus.Declined, WaitlistOfferStatus.Offered },
        { WaitlistOfferStatus.Expired, WaitlistOfferStatus.Offered },
        { WaitlistOfferStatus.Released, WaitlistOfferStatus.Matched },
        // Self.
        { WaitlistOfferStatus.Offered, WaitlistOfferStatus.Offered },
    };

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void AllowedEdge_IsAllowed_AndDoesNotThrow(WaitlistOfferStatus from, WaitlistOfferStatus to)
    {
        Assert.True(WaitlistOfferStateMachine.IsAllowed(from, to));
        WaitlistOfferStateMachine.EnsureAllowed(from, to); // must not throw
    }

    [Theory]
    [MemberData(nameof(DisallowedEdges))]
    public void DisallowedEdge_IsNotAllowed_AndThrows(WaitlistOfferStatus from, WaitlistOfferStatus to)
    {
        Assert.False(WaitlistOfferStateMachine.IsAllowed(from, to));
        var ex = Assert.Throws<InvalidWaitlistOfferTransitionException>(
            () => WaitlistOfferStateMachine.EnsureAllowed(from, to));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
    }

    [Theory]
    [InlineData(WaitlistOfferStatus.Accepted)]
    [InlineData(WaitlistOfferStatus.Declined)]
    [InlineData(WaitlistOfferStatus.Expired)]
    [InlineData(WaitlistOfferStatus.Released)]
    public void TerminalStates_HaveNoOutgoingEdges(WaitlistOfferStatus terminal)
        => Assert.False(WaitlistOfferStateMachine.AllowedTransitions.ContainsKey(terminal));
}
