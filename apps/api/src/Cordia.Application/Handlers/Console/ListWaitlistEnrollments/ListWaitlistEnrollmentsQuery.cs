using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.ListWaitlistEnrollments;

/// <summary>Waitlist enrollments plus their latest offer state, for the Waitlist board.</summary>
public sealed record ListWaitlistEnrollmentsQuery(
    string TenantId,
    bool ActiveOnly,
    int Limit) : IRequest<IReadOnlyList<WaitlistEnrollmentSummary>>;

public sealed class ListWaitlistEnrollmentsValidator : AbstractValidator<ListWaitlistEnrollmentsQuery>
{
    public const int MaxLimit = 200;

    public ListWaitlistEnrollmentsValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxLimit)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");
    }
}

public sealed class ListWaitlistEnrollmentsQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<ListWaitlistEnrollmentsQuery, IReadOnlyList<WaitlistEnrollmentSummary>>
{
    public Task<IReadOnlyList<WaitlistEnrollmentSummary>> Handle(
        ListWaitlistEnrollmentsQuery request,
        CancellationToken ct)
        => reads.ListWaitlistEnrollmentsAsync(request.TenantId, request.ActiveOnly, request.Limit, ct);
}
