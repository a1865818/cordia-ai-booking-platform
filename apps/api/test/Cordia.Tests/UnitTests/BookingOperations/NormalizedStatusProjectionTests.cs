using Cordia.Application.Handlers.BookingOperations.GetOperationStatus;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingOperations;

/// <summary>
/// Exhaustive unit tests for the Inv-16 normalized-status projection in
/// <see cref="GetOperationStatusQueryHandler"/> (spec 06 §5).
///
/// Coverage:
///   • All 16 <see cref="OperationStatus"/> values map to the correct 9-value
///     <see cref="NormalizedOperationStatusEnum"/> per the handler's switch expression.
///   • Pending/Terminal flags match the handler's terminal-set definition.
///   • Inv-16 safety assertions: Reconciling, ManualReview, CompensationRequired normalize
///     to InProgress and their CustomerMessage MUST NOT contain "confirmed", "booked", or "charged".
///   • Booked CustomerMessage MUST indicate confirmed.
///
/// Spec-compliance fixes (T11 final pass):
///   • AwaitingPayment → awaiting_user_action (§5 projection table: "AwaitingPayment also collapses
///     here" — it is an action prompt to pay the deposit, NOT a generic in_progress).
///   • Waitlisted is a TERMINAL state (§6.1: Waitlisted --> [*]) → Terminal=true / Pending=false.
/// </summary>
[Trait("Category", "Unit")]
public class NormalizedStatusProjectionTests
{
    private const string TenantId = "tenant-1";
    private const string OperationId = "op-proj-1";

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static GetOperationStatusQueryHandler MakeHandler(BookingOperation op)
    {
        var repo = new Mock<IBookingOperationRepository>();
        repo.Setup(r => r.GetAsync(OperationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(op);
        return new GetOperationStatusQueryHandler(repo.Object);
    }

    private static BookingOperation MakeOp(OperationStatus status, string? statusReason = null)
        => new()
        {
            TenantId = TenantId,
            OperationId = OperationId,
            IntentId = "int-proj-1",
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            StatusReason = statusReason,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    // -----------------------------------------------------------------------
    // §5 Projection table — all 16 OperationStatus values
    // -----------------------------------------------------------------------

    public static IEnumerable<object[]> ProjectionCases()
    {
        // Each row: (OperationStatus input, NormalizedOperationStatusEnum expected, bool expectedTerminal)
        // Mappings follow the §5 projection table and §6.1 terminal set exactly.
        yield return [OperationStatus.Accepted, NormalizedOperationStatusEnum.Accepted, false];
        yield return [OperationStatus.Validating, NormalizedOperationStatusEnum.InProgress, false];
        yield return [OperationStatus.ReservingCapacity, NormalizedOperationStatusEnum.InProgress, false];
        yield return [OperationStatus.Held, NormalizedOperationStatusEnum.InProgress, false];
        // §5: AwaitingPayment collapses to awaiting_user_action (action prompt — pay the deposit).
        yield return [OperationStatus.AwaitingPayment, NormalizedOperationStatusEnum.AwaitingUserAction, false];
        yield return [OperationStatus.Finalizing, NormalizedOperationStatusEnum.InProgress, false];
        yield return [OperationStatus.Reconciling, NormalizedOperationStatusEnum.InProgress, false];
        yield return [OperationStatus.CompensationRequired, NormalizedOperationStatusEnum.InProgress, false];
        yield return [OperationStatus.ManualReview, NormalizedOperationStatusEnum.InProgress, false];
        yield return [OperationStatus.AwaitingUserAction, NormalizedOperationStatusEnum.AwaitingUserAction, false];
        yield return [OperationStatus.AlternativesRequired, NormalizedOperationStatusEnum.AlternativesRequired, false];
        yield return [OperationStatus.Booked, NormalizedOperationStatusEnum.Booked, true];
        yield return [OperationStatus.Rejected, NormalizedOperationStatusEnum.Rejected, true];
        yield return [OperationStatus.Expired, NormalizedOperationStatusEnum.Expired, true];
        // §6.1: Waitlisted is a terminal state.
        yield return [OperationStatus.Waitlisted, NormalizedOperationStatusEnum.Waitlisted, true];
        yield return [OperationStatus.FailedFinal, NormalizedOperationStatusEnum.Failed, true];
    }

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public async Task Handle_ReturnsCorrectNormalizedStatus(
        OperationStatus internalStatus,
        NormalizedOperationStatusEnum expectedNormalized,
        bool expectedTerminal)
    {
        var op = MakeOp(internalStatus);
        var handler = MakeHandler(op);

        var result = await handler.Handle(new GetOperationStatusQuery(TenantId, OperationId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expectedNormalized, result.Status);
        Assert.Equal(expectedTerminal, result.Terminal);
        Assert.Equal(!expectedTerminal, result.Pending);
    }

    // -----------------------------------------------------------------------
    // Inv-16 safety assertions
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(OperationStatus.Reconciling)]
    [InlineData(OperationStatus.ManualReview)]
    [InlineData(OperationStatus.CompensationRequired)]
    public async Task Inv16InternalSubstates_CustomerMessageDoesNotLeakTerminalLanguage(OperationStatus status)
    {
        // These three states collapse to InProgress (Inv-16). The CustomerMessage MUST NOT
        // contain words that imply completion ("confirmed", "booked", "charged").
        var op = MakeOp(status);
        var handler = MakeHandler(op);

        var result = await handler.Handle(new GetOperationStatusQuery(TenantId, OperationId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(NormalizedOperationStatusEnum.InProgress, result.Status);

        var msg = result.CustomerMessage ?? string.Empty;
        Assert.DoesNotContain("confirmed", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("booked", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("charged", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Booked_CustomerMessageIndicatesConfirmed()
    {
        var op = MakeOp(OperationStatus.Booked);
        var handler = MakeHandler(op);

        var result = await handler.Handle(new GetOperationStatusQuery(TenantId, OperationId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(NormalizedOperationStatusEnum.Booked, result.Status);
        Assert.True(result.Terminal);
        // Message must convey that the booking is confirmed.
        var msg = result.CustomerMessage ?? string.Empty;
        Assert.True(
            msg.Contains("confirmed", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("booked", StringComparison.OrdinalIgnoreCase),
            $"Expected Booked message to indicate confirmed/booked. Actual: \"{msg}\"");
    }

    // -----------------------------------------------------------------------
    // Cross-tenant isolation — op with wrong tenant returns null
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Handle_WrongTenant_ReturnsNull()
    {
        var op = MakeOp(OperationStatus.Booked);
        op.TenantId = "other-tenant";
        var handler = MakeHandler(op);

        var result = await handler.Handle(new GetOperationStatusQuery(TenantId, OperationId), CancellationToken.None);

        Assert.Null(result);
    }
}
