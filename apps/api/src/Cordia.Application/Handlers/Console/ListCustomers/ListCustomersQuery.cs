using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.ListCustomers;

/// <summary>
/// Customers derived from booking history for the console's Customers screen.
///
/// There is no customers table — spec 12 owns all DDL and does not ratify one — so this is
/// an aggregate over booking operations and their confirmed intent revisions, keyed by the
/// opaque <c>customer_ref</c>. It deliberately exposes no name, phone or email (spec 14
/// minimisation); contact details remain behind the verification-gated booking read.
/// </summary>
public sealed record ListCustomersQuery(
    string TenantId,
    string? Query,
    int Limit) : IRequest<CustomerListResult>;

public sealed class ListCustomersValidator : AbstractValidator<ListCustomersQuery>
{
    public const int MaxLimit = 200;
    public const int MaxQueryLength = 128;

    public ListCustomersValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxLimit)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");

        RuleFor(x => x.Query)
            .MaximumLength(MaxQueryLength)
            .When(x => x.Query is not null);
    }
}

public sealed class ListCustomersQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<ListCustomersQuery, CustomerListResult>
{
    public Task<CustomerListResult> Handle(ListCustomersQuery request, CancellationToken ct)
        => reads.ListCustomersAsync(request.TenantId, request.Query, request.Limit, ct);
}
