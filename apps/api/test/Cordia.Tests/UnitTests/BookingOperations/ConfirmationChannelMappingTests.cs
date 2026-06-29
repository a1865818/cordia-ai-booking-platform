using Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;
using Cordia.Domain.Generated;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// Unit tests for <see cref="SubmitConfirmedIntentCommandHandler.MapConfirmationChannel"/> (spec 04 Fix 1).
/// Verifies that every <see cref="ChannelEnum"/> member maps to the lowercase wire vocabulary
/// used by the agent (<c>email | sms | voice | web</c>) rather than the PascalCase C# member
/// name emitted by <c>.ToString()</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ConfirmationChannelMappingTests
{
    [Theory]
    [InlineData(ChannelEnum.Email, "email")]
    [InlineData(ChannelEnum.Sms, "sms")]
    [InlineData(ChannelEnum.Voice, "voice")]
    [InlineData(ChannelEnum.Web, "web")]
    public void MapConfirmationChannel_ReturnsLowercaseWireLabel(ChannelEnum channel, string expected)
    {
        var result = SubmitConfirmedIntentCommandHandler.MapConfirmationChannel(channel);

        Assert.Equal(expected, result);
    }
}
