using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.GetCustomer;

/// <summary>
/// One derived customer for the console detail pane: booking history plus operator notes.
///
/// Does not return unmasked phone or email — <c>customer_ref</c> is minimised at the read
/// model the same way the list is (spec 14). GuestContact stays write-only.
/// </summary>
public sealed record GetCustomerQuery(string TenantId, string CustomerRef) : IRequest<CustomerSummary?>;

public sealed class GetCustomerValidator : AbstractValidator<GetCustomerQuery>
{
    public const int MaxRefLength = 128;

    public GetCustomerValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CustomerRef).NotEmpty().MaximumLength(MaxRefLength);
    }
}

public sealed class GetCustomerQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<GetCustomerQuery, CustomerSummary?>
{
    public Task<CustomerSummary?> Handle(GetCustomerQuery request, CancellationToken ct)
        => reads.GetCustomerAsync(request.TenantId, request.CustomerRef, ct);
}
