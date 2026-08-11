using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Console.ListBookingSchedule;

/// <summary>
/// Bookings starting inside [From, To) for the console's schedule grid.
///
/// Read-only: this reports persisted state and never transitions anything (Inv-16).
/// </summary>
public sealed record ListBookingScheduleQuery(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Limit) : IRequest<ScheduleResult>;

public sealed class ListBookingScheduleValidator : AbstractValidator<ListBookingScheduleQuery>
{
    /// <summary>Widest window a single request may span — a quarter, generously.</summary>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(100);

    public const int MaxLimit = 500;

    public ListBookingScheduleValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Limit)
            .GreaterThan(0)
            .LessThanOrEqualTo(MaxLimit)
            .WithMessage($"limit must be between 1 and {MaxLimit}.");

        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithMessage("to must be after from.");

        RuleFor(x => x)
            .Must(x => x.To - x.From <= MaxWindow)
            .WithMessage($"the requested window must not exceed {MaxWindow.TotalDays:0} days.");
    }
}

public sealed class ListBookingScheduleQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<ListBookingScheduleQuery, ScheduleResult>
{
    public Task<ScheduleResult> Handle(
        ListBookingScheduleQuery request,
        CancellationToken ct)
        => reads.ListScheduleAsync(request.TenantId, request.From, request.To, request.Limit, ct);
}
