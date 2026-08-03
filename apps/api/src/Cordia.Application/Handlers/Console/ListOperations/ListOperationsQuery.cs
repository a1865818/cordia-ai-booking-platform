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
    /// The statuses an operator is expected to act on (spec 06 §11): everything that is
    /// waiting on a person or has stopped making progress on its own.
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

    public Task<IReadOnlyList<OperationSummary>> Handle(ListOperationsQuery request, CancellationToken ct)
        => reads.ListOperationsAsync(request.TenantId, request.Statuses, request.Limit, ct);
}
