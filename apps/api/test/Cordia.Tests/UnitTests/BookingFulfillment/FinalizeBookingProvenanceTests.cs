using Cordia.Application.Handlers.BookingFulfillment.Finalize;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>G12: finalize copies the operation's attributed source onto the booking aggregate.</summary>
[Trait("Category", "Unit")]
public class FinalizeBookingProvenanceTests
{
    [Fact]
    public void BuildBookingAggregate_CopiesAttributedSourceFromOperation()
    {
        var now = DateTimeOffset.UtcNow;
        var operation = new BookingOperation
        {
            AttributedSource = AttributedSource.Operator,
            CreatedAt = now.AddMinutes(-5),
        };

        var booking = FinalizeBookingCommandHandler.BuildBookingAggregate(
            "tenant-a",
            operation,
            "bk-1",
            """{"booking_reference":"ref-1","customer_id":"cust-1"}""",
            now);

        Assert.Equal(AttributedSource.Operator, booking.AttributedSource);
        Assert.Equal("bk-1", booking.BookingId);
        Assert.Equal("cust-1", booking.CustomerId);
    }

    [Fact]
    public void BuildBookingAggregate_LeavesSourceNullWhenOperationHasNone()
    {
        var booking = FinalizeBookingCommandHandler.BuildBookingAggregate(
            "tenant-a",
            new BookingOperation { CreatedAt = DateTimeOffset.UtcNow },
            "bk-1",
            "{}",
            DateTimeOffset.UtcNow);

        Assert.Null(booking.AttributedSource);
    }
}
