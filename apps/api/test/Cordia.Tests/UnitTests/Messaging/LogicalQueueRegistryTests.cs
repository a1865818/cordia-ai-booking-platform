using Cordia.Domain.Messaging;

namespace Cordia.Tests.UnitTests.Messaging;

[Trait("Category", "Unit")]
public class LogicalQueueRegistryTests
{
    [Theory]
    [InlineData("booking.command.requested.v1", LogicalQueue.BookingCommands)]
    [InlineData("booking.finalize.requested.v1", LogicalQueue.BookingCommands)]
    [InlineData("booking.manage.cancel.v1", LogicalQueue.BookingCommands)]
    [InlineData("booking.reconcile.requested.v1", LogicalQueue.BookingReconcile)]
    [InlineData("booking.scheduled.hold_expiry.v1", LogicalQueue.BookingScheduled)]
    [InlineData("booking.compensate.hold_release.v1", LogicalQueue.BookingCompensate)]
    [InlineData("notification.send.requested.v1", LogicalQueue.NotificationCommands)]
    [InlineData("waitlist.offer.requested.v1", LogicalQueue.WaitlistCommands)]
    [InlineData("menu.preorder.requested.v1", LogicalQueue.MenuCommands)]
    [InlineData("operations.review.created.v1", LogicalQueue.OperationsReview)]
    public void Resolve_RoutesMessageType_ToExpectedQueue(string messageType, LogicalQueue expected)
    {
        var result = LogicalQueueRegistry.Resolve(messageType);

        Assert.Equal(expected, result);
    }

    // Boundary: a prefix-lookalike that is NOT the dotted sub-prefix must fall through to the
    // generic booking queue, not be swallowed by booking.reconcile. The trailing dot in each
    // routing prefix protects this — pin it with a regression test.
    [Theory]
    [InlineData("booking.reconcilex.v1", LogicalQueue.BookingCommands)]
    [InlineData("booking.scheduledx.v1", LogicalQueue.BookingCommands)]
    [InlineData("booking.compensatex.v1", LogicalQueue.BookingCommands)]
    public void Resolve_PrefixLookalike_FallsThroughToBookingCommands(string messageType, LogicalQueue expected)
    {
        Assert.Equal(expected, LogicalQueueRegistry.Resolve(messageType));
    }

    [Fact]
    public void Resolve_Throws_ForUnroutableMessageType()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LogicalQueueRegistry.Resolve("unknown.thing.v1"));

        Assert.Contains("unknown.thing.v1", ex.Message);
    }

    [Fact]
    public void TryResolve_ReturnsFalse_ForUnroutableMessageType()
    {
        var resolved = LogicalQueueRegistry.TryResolve("unknown.thing.v1", out var queue);

        Assert.False(resolved);
        Assert.Equal(default, queue);
    }

    [Fact]
    public void All_HasNineEntries()
    {
        // 8 base lanes + the spec-11 preorder.commands lane.
        Assert.Equal(9, LogicalQueueRegistry.All.Count);
    }

    [Fact]
    public void Resolve_PreorderPrefix_RoutesToPreorderCommands()
    {
        Assert.True(LogicalQueueRegistry.TryResolve("preorder.commands.start.v1", out var queue));
        Assert.Equal(LogicalQueue.PreorderCommands, queue);
    }

    [Fact]
    public void WireName_BookingCommands_ReturnsExpectedWireName()
    {
        Assert.Equal("booking.commands", LogicalQueueRegistry.WireName(LogicalQueue.BookingCommands));
    }

    [Fact]
    public void Get_ReturnsCorrectMetadata_ForEachQueue()
    {
        foreach (var meta in LogicalQueueRegistry.All)
        {
            var fetched = LogicalQueueRegistry.Get(meta.Queue);
            Assert.Equal(meta.Queue, fetched.Queue);
            Assert.Equal(meta.Name, fetched.Name);
        }
    }

    [Fact]
    public void TryGetByWireName_KnownName_ReturnsTrueAndCorrectQueue()
    {
        var found = LogicalQueueRegistry.TryGetByWireName("booking.scheduled", out var queue);

        Assert.True(found);
        Assert.Equal(LogicalQueue.BookingScheduled, queue);
    }

    [Fact]
    public void TryGetByWireName_UnknownName_ReturnsFalse()
    {
        var found = LogicalQueueRegistry.TryGetByWireName("not.a.real.queue", out var queue);

        Assert.False(found);
        Assert.Equal(default, queue);
    }
}
