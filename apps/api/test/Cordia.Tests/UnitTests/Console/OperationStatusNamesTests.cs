using Cordia.Domain.Enums;

namespace Cordia.Tests.UnitTests.Console;

/// <summary>
/// Wire-name round-trip for <see cref="OperationStatus"/> (D1 — the 16 ratified values).
///
/// The console filters operations by status over the query string, so the snake_case wire
/// names are part of the HTTP contract. These tests pin them: renaming a CLR member without
/// updating its <c>JsonStringEnumMemberName</c> would silently change the API's accepted
/// filter values.
/// </summary>
[Trait("Category", "Unit")]
public class OperationStatusNamesTests
{
    [Fact]
    public void AllSixteenRatifiedValuesAreExposed()
    {
        Assert.Equal(16, OperationStatusNames.All.Count);
        Assert.Equal(
            [
                "accepted",
                "validating",
                "awaiting_user_action",
                "reserving_capacity",
                "held",
                "awaiting_payment",
                "finalizing",
                "booked",
                "alternatives_required",
                "reconciling",
                "compensation_required",
                "manual_review",
                "rejected",
                "expired",
                "waitlisted",
                "failed_final",
            ],
            OperationStatusNames.All);
    }

    [Fact]
    public void EveryValueRoundTrips()
    {
        foreach (var status in Enum.GetValues<OperationStatus>())
        {
            var wire = OperationStatusNames.ToWireName(status);
            Assert.True(OperationStatusNames.TryParse(wire, out var parsed), $"failed to parse '{wire}'");
            Assert.Equal(status, parsed);
        }
    }

    [Theory]
    [InlineData("AWAITING_USER_ACTION")]
    [InlineData("Awaiting_User_Action")]
    [InlineData("  awaiting_user_action  ")]
    public void ParseIsCaseInsensitiveAndTrims(string value)
    {
        Assert.True(OperationStatusNames.TryParse(value, out var parsed));
        Assert.Equal(OperationStatus.AwaitingUserAction, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AwaitingUserAction")] // the CLR name is deliberately NOT accepted
    [InlineData("nonsense")]
    public void ParseRejectsUnknownValues(string? value)
    {
        Assert.False(OperationStatusNames.TryParse(value, out _));
    }
}
