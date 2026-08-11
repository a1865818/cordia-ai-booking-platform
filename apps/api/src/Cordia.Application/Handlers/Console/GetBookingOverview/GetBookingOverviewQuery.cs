using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.GetBookingOverview;

/// <summary>
/// Per-day, per-channel and per-service aggregates over [From, To) for the Analytics screen.
///
/// Complements <c>GET /api/reporting/funnel</c> (spec 13), which owns the separated funnel
/// counts; this query adds only the breakdowns the console charts need and never collapses
/// funnel stages into a single number (Inv-16).
/// </summary>
public sealed record GetBookingOverviewQuery(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<BookingOverview>;

public sealed class GetBookingOverviewValidator : AbstractValidator<GetBookingOverviewQuery>
{
    /// <summary>Widest window a single request may span.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(400);

    public GetBookingOverviewValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithMessage("to must be after from.");

        RuleFor(x => x)
            .Must(x => x.To - x.From <= MaxWindow)
            .WithMessage($"the requested window must not exceed {MaxWindow.TotalDays:0} days.");
    }
}

public sealed class GetBookingOverviewQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<GetBookingOverviewQuery, BookingOverview>
{
    public Task<BookingOverview> Handle(GetBookingOverviewQuery request, CancellationToken ct)
        => reads.GetOverviewAsync(request.TenantId, request.From, request.To, ct);
}
