using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.ManageBooking.FindManageableBookings;
using Cordia.Domain.Exceptions;
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
    IGrantAuthorizationService grants,
    IAuditWriter audit)
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

        // The optimistic-concurrency token callers must echo back as expected_booking_version
        // lives on the bookings row, not on the operation (spec 08 §9.4.8). Without it there is
        // no way for a client to issue a modify/cancel that is not guaranteed to 409, so it is
        // part of the minimal projection. It is null when no authoritative row exists yet
        // (e.g. an operation still in flight), which callers must treat as "not modifiable yet".
        var authoritative = await bookings.GetByIdAsync(request.TenantId, projection.BookingId, ct);

        var minimal = ManageBookingMapper.ToContract(projection, authoritative?.Version);
        var grantId = string.IsNullOrWhiteSpace(request.GrantId) ? null : request.GrantId;
        if (grantId is null)
            return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.Ok, minimal);

        if (authoritative is null || string.IsNullOrWhiteSpace(authoritative.CustomerId))
            return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.RequiresVerification, minimal);

        try
        {
            await grants.RequireGrantAsync(
                request.TenantId,
                grantId,
                authoritative.CustomerId,
                VerificationActionEnum.ReadSensitive,
                projection.BookingId,
                targetType: "booking",
                ct: ct);
        }
        catch (VerificationRequiredException)
        {
            // The deny decision was audited inside GrantAuthorizationService.
            return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.RequiresVerification, minimal);
        }

        // Spec 14 §3.1 step 5 / §5.1 — sensitive READS are audited like writes.
        await audit.WriteAsync(
            new AuditRecord(
                TenantId: request.TenantId,
                ActorType: "customer",
                ActorId: authoritative.CustomerId,
                Action: "read_sensitive_booking",
                TargetType: "booking",
                TargetId: projection.BookingId,
                Decision: "allow",
                DataClasses: ["pii", "booking"],
                Extra: new { grant_id = grantId }),
            ct);

        return new GetBookingDetailsResult(GetBookingDetailsStatusEnum.Ok, minimal);
    }
}
