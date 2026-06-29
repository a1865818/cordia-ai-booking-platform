using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.ManageBooking.FindManageableBookings;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.GetBookingDetails;

/// <summary>Query for a verification-leveled booking details projection.</summary>
public sealed record GetBookingDetailsQuery(
    string TenantId,
    string BookingId,
    string? GrantId) : IRequest<GetBookingDetailsResult>;

public sealed record GetBookingDetailsResult(
    GetBookingDetailsStatusEnum Status,
    BookingProjectionRef? Booking);

public sealed class GetBookingDetailsQueryHandler(
    IBookingReadRepository readBookings,
    IBookingRepository bookings,
    IGrantAuthorizationService grants)
    : IRequestHandler<GetBookingDetailsQuery, GetBookingDetailsResult>
{
    public async Task<GetBookingDetailsResult> Handle(GetBookingDetailsQuery request, CancellationToken ct)
    {
        var projection = await readBookings.GetBookingProjectionByBookingIdAsync(
            request.TenantId,
            request.BookingId,
            ct);

        if (projection is null)
            return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.NotFound, null);

        var minimal = ManageBookingMapper.ToContract(projection);
        var grantId = EmptyToNull(request.GrantId);
        if (grantId is null)
            return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.Ok, minimal);

        var authoritative = await bookings.GetByIdAsync(request.TenantId, projection.BookingId, ct);
        if (authoritative is null || string.IsNullOrWhiteSpace(authoritative.CustomerId))
            return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.RequiresVerification, minimal);

        await grants.RequireGrantAsync(
            request.TenantId,
            grantId,
            authoritative.CustomerId,
            VerificationActionEnum.ReadSensitive,
            projection.BookingId,
            ct);

        return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.Ok, minimal);
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
