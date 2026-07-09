using Cordia.Application.Handlers.Preorder;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §5.3 (divergence #2) — the single gate every 06 emit site uses to decide the booking
/// outcome message. A pre-order operation signals the saga (which emits the one combined customer
/// outcome, Inv-8); a normal operation emits the customer notification directly. Keeping the
/// non-pre-order mapping byte-identical to the previous literals is what lets the existing 06 handler
/// tests (which use non-pre-order operations) stay green.
/// </summary>
[Trait("Category", "Unit")]
public class PreorderOutcomeRoutingTests
{
    [Fact]
    public void ConfirmedOutcome_Preorder_SignalsSaga()
        => Assert.Equal("preorder.commands.booking_committed.v1",
            PreorderSagaMessages.ConfirmedOutcomeType(isPreorder: true));

    [Fact]
    public void ConfirmedOutcome_NonPreorder_NotifiesCustomer()
        => Assert.Equal("notification.booking_confirmed.v1",
            PreorderSagaMessages.ConfirmedOutcomeType(isPreorder: false));

    [Fact]
    public void FailedOutcome_Preorder_SignalsSaga()
        => Assert.Equal("preorder.commands.booking_failed.v1",
            PreorderSagaMessages.FailedOutcomeType(isPreorder: true));

    [Fact]
    public void FailedOutcome_NonPreorder_NotifiesCustomer()
        => Assert.Equal("notification.booking_failed.v1",
            PreorderSagaMessages.FailedOutcomeType(isPreorder: false));
}
