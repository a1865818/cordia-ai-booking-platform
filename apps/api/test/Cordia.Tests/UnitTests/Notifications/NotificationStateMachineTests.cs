using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Tests.UnitTests.Notifications;

/// <summary>
/// Unit tests for <see cref="NotificationStateMachine"/> and <see cref="Notification.TransitionTo"/>
/// (spec 10 §6.1). Covers: every allowed edge, representative forbidden edges, terminal states,
/// and <see cref="Notification.TransitionTo"/> side effects.
/// </summary>
[Trait("Category", "Unit")]
public class NotificationStateMachineTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Notification MakeNotification(NotificationState state) =>
        new() { State = state, UpdatedAt = DateTimeOffset.MinValue };

    // -----------------------------------------------------------------------
    // Every defined edge is allowed
    // -----------------------------------------------------------------------

    public static TheoryData<NotificationState, NotificationState> AllowedEdges()
    {
        var data = new TheoryData<NotificationState, NotificationState>();
        foreach (var (from, targets) in NotificationStateMachine.AllowedTransitions)
            foreach (var to in targets)
                data.Add(from, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void IsAllowed_ReturnsTrue_ForEveryDefinedEdge(NotificationState from, NotificationState to)
        => Assert.True(NotificationStateMachine.IsAllowed(from, to));

    [Theory]
    [MemberData(nameof(AllowedEdges))]
    public void EnsureAllowed_DoesNotThrow_ForEveryDefinedEdge(NotificationState from, NotificationState to)
    {
        // Should not throw
        NotificationStateMachine.EnsureAllowed(from, to);
    }

    // -----------------------------------------------------------------------
    // Representative undefined edges throw InvalidNotificationStateTransitionException
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(NotificationState.Pending, NotificationState.Delivered)]
    [InlineData(NotificationState.Pending, NotificationState.Accepted)]
    [InlineData(NotificationState.Pending, NotificationState.FailedPermanent)]
    [InlineData(NotificationState.Sending, NotificationState.Pending)]
    [InlineData(NotificationState.Sending, NotificationState.SuppressedDuplicate)]
    [InlineData(NotificationState.Accepted, NotificationState.Sending)]
    [InlineData(NotificationState.Accepted, NotificationState.Pending)]
    [InlineData(NotificationState.FailedTransient, NotificationState.Pending)]
    [InlineData(NotificationState.FailedTransient, NotificationState.Accepted)]
    [InlineData(NotificationState.Delivered, NotificationState.Accepted)]
    [InlineData(NotificationState.FailedPermanent, NotificationState.Sending)]
    [InlineData(NotificationState.SuppressedDuplicate, NotificationState.Pending)]
    [InlineData(NotificationState.InvalidRecipient, NotificationState.Sending)]
    public void EnsureAllowed_Throws_ForUndefinedEdge(NotificationState from, NotificationState to)
    {
        var ex = Assert.Throws<InvalidNotificationStateTransitionException>(
            () => NotificationStateMachine.EnsureAllowed(from, to));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
    }

    // -----------------------------------------------------------------------
    // Terminal states have no outgoing edges
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(NotificationState.SuppressedDuplicate)]
    [InlineData(NotificationState.InvalidRecipient)]
    [InlineData(NotificationState.Delivered)]
    [InlineData(NotificationState.FailedPermanent)]
    public void TerminalState_HasNoOutgoingEdges(NotificationState terminal)
    {
        Assert.False(NotificationStateMachine.AllowedTransitions.ContainsKey(terminal));
    }

    // -----------------------------------------------------------------------
    // Notification.TransitionTo — valid edge applies side effects
    // -----------------------------------------------------------------------

    [Fact]
    public void TransitionTo_ValidEdge_AppliesStateAndUpdatedAt()
    {
        var notification = MakeNotification(NotificationState.Pending);
        var before = DateTimeOffset.UtcNow;

        notification.TransitionTo(NotificationState.Sending);

        Assert.Equal(NotificationState.Sending, notification.State);
        Assert.True(notification.UpdatedAt >= before);
    }

    [Fact]
    public void TransitionTo_WithFailureCategory_SetsFailureCategory()
    {
        var notification = MakeNotification(NotificationState.Sending);

        notification.TransitionTo(NotificationState.FailedTransient, failureCategory: "provider_timeout");

        Assert.Equal(NotificationState.FailedTransient, notification.State);
        Assert.Equal("provider_timeout", notification.FailureCategory);
    }

    [Fact]
    public void TransitionTo_WithoutFailureCategory_DoesNotOverwriteExisting()
    {
        var notification = MakeNotification(NotificationState.Sending);
        notification.FailureCategory = "previous";

        notification.TransitionTo(NotificationState.Accepted);

        Assert.Equal(NotificationState.Accepted, notification.State);
        // FailureCategory not overwritten when null is passed
        Assert.Equal("previous", notification.FailureCategory);
    }

    [Fact]
    public void TransitionTo_DoesNotBumpVersion()
    {
        var notification = MakeNotification(NotificationState.Pending);
        notification.Version = 3L;

        notification.TransitionTo(NotificationState.Sending);

        Assert.Equal(3L, notification.Version);
    }

    // -----------------------------------------------------------------------
    // Notification.TransitionTo — invalid edge throws
    // -----------------------------------------------------------------------

    [Fact]
    public void TransitionTo_InvalidEdge_ThrowsInvalidNotificationStateTransitionException()
    {
        var notification = MakeNotification(NotificationState.Delivered);

        var ex = Assert.Throws<InvalidNotificationStateTransitionException>(
            () => notification.TransitionTo(NotificationState.Sending));

        Assert.Equal(NotificationState.Delivered, ex.From);
        Assert.Equal(NotificationState.Sending, ex.To);
    }
}
