using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;

public sealed class ValidateBookingChangeQueryHandler(
    IBookingRepository bookings,
    IGrantAuthorizationService grants)
    : IRequestHandler<ValidateBookingChangeQuery, ValidateBookingChangeResult>
{
    public async Task<ValidateBookingChangeResult> Handle(
        ValidateBookingChangeQuery request,
        CancellationToken ct)
    {
        var booking = await bookings.GetByIdAsync(request.TenantId, request.TargetBookingId, ct)
            ?? throw new OperationConflictException(request.TargetBookingId, "Booking was not found.");

        await grants.RequireGrantAsync(
            request.TenantId,
            request.GrantId,
            booking.CustomerId,
            VerificationActionEnum.Modify,
            request.TargetBookingId,
            ct);

        if (booking.Version != request.ExpectedBookingVersion)
        {
            return new ValidateBookingChangeResult(
                "stale_version",
                BuildValidation(
                    valid: false,
                    ValidateBookingChangeStatusEnum.StaleVersion,
                    requiresConfirmation: false,
                    recoverableFields: ["expected_booking_version"]));
        }

        return new ValidateBookingChangeResult(
            "ok",
            BuildValidation(
                valid: true,
                ValidateBookingChangeStatusEnum.Valid,
                requiresConfirmation: true,
                recoverableFields: []));
    }

    private static BookingChangeValidation BuildValidation(
        bool valid,
        ValidateBookingChangeStatusEnum code,
        bool requiresConfirmation,
        List<string> recoverableFields)
        => new()
        {
            Valid = valid,
            Code = code,
            FeesMinor = 0,
            Currency = "AUD",
            RequiresConfirmation = requiresConfirmation,
            RequiresPayment = false,
            RecoverableFields = recoverableFields,
            PolicyVersion = "manage-booking.v1",
            CapacityMode = AvailabilityModeEnum.Live,
        };
}
