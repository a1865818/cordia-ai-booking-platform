using Cordia.Api.Controllers;
using Cordia.Application.Handlers.BookingFulfillment.Cancellation;
using Cordia.Application.Handlers.BookingOperations.GetOperationStatus;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Pins what <c>POST /api/booking-operations/{id}/cancel</c> tells the caller (#53, Inv-16).
///
/// <para><b>The defect.</b> The endpoint guards terminality up front, but from a snapshot read by a
/// separate query. Between that read and the write, the workflow driver can legally commit a
/// terminal transition — <c>Finalizing→Booked</c> and <c>AlternativesRequired→FailedFinal</c> are
/// both edges in <c>OperationStateMachine</c>. When it does,
/// <c>RequestCancellationCommandHandler</c> correctly returns <c>false</c> (its repository filter is
/// <c>CancellationRequestedAt == null AND NOT terminal</c>), and the controller discarded that and
/// hardcoded <c>cancellation_pending = true</c> — reporting a pending cancellation on an operation
/// that had already finished.</para>
///
/// <para><b>Why these are unit tests and not a real race.</b> Reproducing the window through the
/// HTTP stack means winning a timing race on every run. This repo has already declined that kind of
/// test once, on the grounds that it is inherently flaky. Mocking <see cref="IMediator"/> makes the
/// window deterministic: the second <c>GetOperationStatusQuery</c> simply answers differently from
/// the first, which is exactly what a lost race looks like from the controller's side.</para>
///
/// <para>All four outcomes of the branch are covered, because the handler's <c>false</c> is
/// ambiguous by design — it means "already pending" <i>or</i> "raced into terminal", and those need
/// different answers.</para>
/// </summary>
[Trait("Category", "Unit")]
public class CancellationOutcomeTests
{
    private const string TenantId = "t-cancel";
    private const string OperationId = "op-cancel-1";

    private readonly Mock<IMediator> _mediator = new();
    private readonly Mock<ITenantContext> _tenant = new();
    private readonly Mock<IActorContext> _actor = new();

    public CancellationOutcomeTests() => _tenant.SetupGet(t => t.TenantId).Returns(TenantId);

    private BookingOperationsController CreateController() =>
        new(_mediator.Object, _tenant.Object, _actor.Object,
            Microsoft.Extensions.Options.Options.Create(new Cordia.Application.Common.Authorization.AssertedActorOptions()));

    private static OperationStatusView View(bool terminal) =>
        new()
        {
            OperationId = OperationId,
            Terminal = terminal,
            Pending = !terminal,
            CustomerMessage = string.Empty,
            BookingId = string.Empty,
            BookingReference = string.Empty,
        };

    /// <summary>Queues the status answers in order: the up-front snapshot, then any re-read.</summary>
    private void SetupStatusSequence(params OperationStatusView?[] answers)
    {
        var seq = _mediator.SetupSequence(m => m.Send(
            It.IsAny<GetOperationStatusQuery>(), It.IsAny<CancellationToken>()));

        foreach (var answer in answers)
            seq = seq.ReturnsAsync(answer);
    }

    private void SetupCancellationResult(bool newlySet) =>
        _mediator
            .Setup(m => m.Send(It.IsAny<RequestCancellationCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(newlySet);

    // ── The bug: the operation goes terminal inside the window ─────────────────────

    /// <summary>
    /// The regression test for #53. Non-terminal at the guard, terminal by the time the flag is
    /// written. Before the fix this returned 202 with <c>cancellation_pending: true</c>.
    /// </summary>
    [Fact]
    public async Task RacedIntoTerminal_Returns409_NotAFalsePending()
    {
        SetupStatusSequence(View(terminal: false), View(terminal: true));
        SetupCancellationResult(newlySet: false);

        var result = await CreateController().RequestCancellation(OperationId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    // ── The other three outcomes ───────────────────────────────────────────────────

    [Fact]
    public async Task FlagNewlySet_Returns202WithPendingTrue()
    {
        SetupStatusSequence(View(terminal: false));
        SetupCancellationResult(newlySet: true);

        var result = await CreateController().RequestCancellation(OperationId, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.True(PendingFlagOf(accepted.Value));
    }

    /// <summary>
    /// Idempotency (§15.5): a second request against a still-running operation is not an error, and
    /// <c>cancellation_pending: true</c> is honest here — one really is pending.
    /// </summary>
    [Fact]
    public async Task AlreadyPendingAndStillRunning_Returns202WithPendingTrue()
    {
        SetupStatusSequence(View(terminal: false), View(terminal: false));
        SetupCancellationResult(newlySet: false);

        var result = await CreateController().RequestCancellation(OperationId, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.True(PendingFlagOf(accepted.Value));
    }

    /// <summary>A row that vanishes inside the window is a 404, not a fabricated 202.</summary>
    [Fact]
    public async Task DisappearsInsideTheWindow_Returns404()
    {
        SetupStatusSequence(View(terminal: false), null);
        SetupCancellationResult(newlySet: false);

        var result = await CreateController().RequestCancellation(OperationId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The 202 body is an anonymous type (the endpoint is <c>IActionResult</c>, so the OpenAPI
    /// document carries no schema for it), so the flag is read reflectively rather than by cast.
    /// </summary>
    private static bool PendingFlagOf(object? body)
    {
        Assert.NotNull(body);
        var prop = body!.GetType().GetProperty("cancellation_pending");
        Assert.NotNull(prop);
        return (bool)prop!.GetValue(body)!;
    }
}
