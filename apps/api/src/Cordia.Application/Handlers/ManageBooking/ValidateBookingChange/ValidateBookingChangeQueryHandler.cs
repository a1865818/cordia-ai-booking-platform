using System.Text.Json;
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
        if (!string.Equals(request.RouteBookingId, request.TargetBookingId, StringComparison.Ordinal))
            throw new OperationConflictException(request.RouteBookingId, "Route booking id does not match request target_booking_id.");

        var booking = await bookings.GetByIdAsync(request.TenantId, request.TargetBookingId, ct)
            ?? throw new OperationConflictException(request.TargetBookingId, "Booking was not found.");

        await grants.RequireGrantAsync(
            request.TenantId,
            request.GrantId,
            booking.CustomerId,
            VerificationActionEnum.Modify,
            request.TargetBookingId,
            targetType: "booking",
            ct: ct);

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

        var requestedFields = GetRequestedFieldNames(request.RequestedFields);
        var unsupportedFields = requestedFields
            .Where(f => !SupportedChangeFields.Contains(f))
            .ToList();

        if (requestedFields.Count == 0 || unsupportedFields.Count > 0)
        {
            return new ValidateBookingChangeResult(
                "policy_failed",
                BuildValidation(
                    valid: false,
                    ValidateBookingChangeStatusEnum.PolicyFailed,
                    requiresConfirmation: false,
                    recoverableFields: requestedFields.Count == 0 ? ["requested_fields"] : unsupportedFields));
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

    private static readonly HashSet<string> SupportedChangeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "starts_at",
        "start_at",
        "booking_date",
        "booking_time",
        "approx_time",
        "party_size",
        "special_requests",
    };

    private static List<string> GetRequestedFieldNames(JsonElement requestedFields)
    {
        if (requestedFields.ValueKind != JsonValueKind.Object)
            return [];

        return requestedFields
            .EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }
}
