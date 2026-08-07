using Cordia.Domain.Console;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.ListOperations;

/// <summary>
/// Booking operations for the console's Queue / Operations board.
///
/// <see cref="Statuses"/> is the parsed subset of the ratified <see cref="OperationStatus"/>
/// set (D1, owner spec 06). An unparseable status is rejected at the controller edge rather
/// than silently ignored, so a typo cannot quietly widen the result to "everything".
/// </summary>
public sealed record ListOperationsQuery(
    string TenantId,
    IReadOnlyCollection<OperationStatus> Statuses,
    int Limit) : IRequest<IReadOnlyList<OperationSummary>>;

public sealed class ListOperationsValidator : AbstractValidator<ListOperationsQuery>
{
    public const int MaxLimit = 500;

    public ListOperationsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxLimit)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");

        RuleFor(x => x.Statuses).NotNull();
    }
}

public sealed class ListOperationsQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<ListOperationsQuery, IReadOnlyList<OperationSummary>>
{
    /// <summary>
    /// The statuses an operator is expected to act on (spec 06 §11): everything that is waiting on
    /// a person or has stopped making progress on its own.
    ///
    /// <para>This one stays a LIST, deliberately — it is a product judgement about what belongs on
    /// a queue board, not a fact the state machine owns. Two of its members make that plain:
    /// <c>FailedFinal</c> is terminal and needs no action from the platform, but an operator
    /// generally wants to see it; and <c>Reconciling</c>/<c>CompensationRequired</c> are actively
    /// driven and usually clear in milliseconds, yet are worth surfacing when they do not.</para>
    ///
    /// <para>What it is NOT allowed to be is silently incomplete.
    /// <c>WorkflowDefinitionTests.NeedsAttention_ClassifiesEveryStatusDeliberately</c> asserts that
    /// every one of D1's 16 statuses is either on this list or on a companion list of statuses
    /// consciously kept OFF it — so adding a status to the enum fails a test until someone decides
    /// which side it belongs on, rather than defaulting it to invisible.</para>
    /// </summary>
    public static readonly IReadOnlyList<OperationStatus> NeedsAttention =
    [
        OperationStatus.AwaitingUserAction,
        OperationStatus.AlternativesRequired,
        OperationStatus.Reconciling,
        OperationStatus.CompensationRequired,
        OperationStatus.ManualReview,
        OperationStatus.FailedFinal,
    ];

    /// <summary>
    /// The statuses deliberately kept OFF the queue board, with the reason they are not an
    /// operator's problem. Exists so the two lists together must cover D1 exhaustively.
    /// </summary>
    public static readonly IReadOnlyList<OperationStatus> DeliberatelyNotSurfaced =
    [
        // Healthy in-flight progress — the driver owns these and they move on their own.
        OperationStatus.Accepted,
        OperationStatus.Validating,
        OperationStatus.ReservingCapacity,
        OperationStatus.Held,
        OperationStatus.Finalizing,
        // Waiting on the customer to pay; chasing it is a notification concern, not a queue item.
        OperationStatus.AwaitingPayment,
        // Happy endings and self-explanatory ones.
        OperationStatus.Booked,
        OperationStatus.Rejected,
        OperationStatus.Expired,
        OperationStatus.Waitlisted,
    ];

    public Task<IReadOnlyList<OperationSummary>> Handle(ListOperationsQuery request, CancellationToken ct)
        => reads.ListOperationsAsync(request.TenantId, request.Statuses, request.Limit, ct);
}
