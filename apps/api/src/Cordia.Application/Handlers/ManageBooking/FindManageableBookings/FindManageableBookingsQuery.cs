using System.Globalization;
using Cordia.Domain.Bookings;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.FindManageableBookings;

/// <summary>Query for minimized Manage Booking discovery candidates.</summary>
public sealed record FindManageableBookingsQuery(
    string TenantId,
    string? PhoneE164,
    string? BookingReference,
    string? Email) : IRequest<FindManageableBookingsResult>;

public sealed record FindManageableBookingsResult(
    FindStatus Status,
    IReadOnlyList<ManageableBooking> Candidates);

public sealed class FindManageableBookingsValidator : AbstractValidator<FindManageableBookingsQuery>
{
    public FindManageableBookingsValidator()
    {
        RuleFor(x => x)
            .Must(HasAnyHint)
            .WithMessage("At least one of phone_e164, booking_reference, or email is required.");
    }

    private static bool HasAnyHint(FindManageableBookingsQuery query)
        => !string.IsNullOrWhiteSpace(query.PhoneE164)
           || !string.IsNullOrWhiteSpace(query.BookingReference)
           || !string.IsNullOrWhiteSpace(query.Email);
}

public sealed class FindManageableBookingsQueryHandler(IBookingReadRepository bookings)
    : IRequestHandler<FindManageableBookingsQuery, FindManageableBookingsResult>
{
    public async Task<FindManageableBookingsResult> Handle(
        FindManageableBookingsQuery request,
        CancellationToken ct)
    {
        var hints = new ManageBookingDiscoveryHints(
            PhoneE164: request.PhoneE164,
            Email: request.Email,
            BookingReference: request.BookingReference);

        var candidates = await bookings.FindManageBookingCandidatesAsync(request.TenantId, hints, ct: ct);
        var mapped = candidates.Select(ManageBookingMapper.ToContract).ToList();

        var status = mapped.Count switch
        {
            0 => FindStatus.None,
            1 => FindStatus.Found,
            _ => FindStatus.Multiple,
        };

        return new FindManageableBookingsResult(status, mapped);
    }
}

internal static class ManageBookingMapper
{
    public static ManageableBooking ToContract(ManageBookingCandidate candidate)
        => new()
        {
            BookingId = candidate.BookingId,
            BookingReference = candidate.BookingReference,
            Business = candidate.BusinessName,
            Date = candidate.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ApproxTime = candidate.ApproxTime,
            PartySize = candidate.PartySize,
        };

    public static BookingProjectionRef ToContract(BookingProjection projection)
        => new()
        {
            BookingId = projection.BookingId,
            BookingReference = projection.BookingReference!,
            Status = projection.LifecycleStatus,
            StartsAt = projection.StartsAt,
            PartySize = projection.PartySize,
        };
}
