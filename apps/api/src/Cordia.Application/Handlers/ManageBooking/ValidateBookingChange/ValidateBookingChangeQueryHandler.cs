using System.Text.Json;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;

public sealed class ValidateBookingChangeQueryHandler(
    IBookingRepository bookings,
    IGrantAuthorizationService grants,
    IOptions<AssertedActorOptions> assertedActor)
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

        // A grant OR an authenticated staff role — the SAME door spec 08 §12 opens for the
        // mutation this call precedes (see SubmitConfirmedIntentCommandHandler.AuthorizeMutationAsync).
        //
        // Requiring a grant here while the acceptance path accepts an operator role made the
        // PRE-FLIGHT stricter than the change it is meant to de-risk: the console, which has no
        // grant, was 403'd from asking "may I?" and could only find out by submitting. A check
        // that only the caller who does not need it can run is not a check. Nothing here is
        // widened by the role: this returns validation flags for a booking the same actor may
        // already modify, discloses no guest data, and writes nothing.
        //
        // "The SAME door" has to include the door's LOCK, not just its shape. The acceptance path
        // gates its role branch on <see cref="AssertedActorOptions.AllowAssertedStaffRole"/>,
        // because `X-Actor-Role` is a string the client chose and `StaffRole.Satisfies` checks the
        // vocabulary, not the caller. Ungated here, the pre-flight stayed open in production after
        // the mutation it precedes had closed — so a caller who could no longer reschedule someone
        // else's booking could still aim this at any booking id in the tenant and read back
        // `stale_version` vs `ok`, i.e. an existence-and-version oracle over the venue's bookings,
        // reached with a tenant header and nothing else (Inv-11). A pre-flight may be no stricter
        // than the mutation; it must not be looser either.
        var staffAuthorized =
            assertedActor.Value.AllowAssertedStaffRole
            && !string.IsNullOrWhiteSpace(request.ActorId)
            && StaffRole.Satisfies(request.ActorRole, StaffRole.Operator);

        if (!staffAuthorized)
        {
            await grants.RequireGrantAsync(
                request.TenantId,
                request.GrantId,
                booking.CustomerId,
                VerificationActionEnum.Modify,
                request.TargetBookingId,
                targetType: "booking",
                ct: ct);
        }

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
        // A reschedule legitimately moves the length as well as the time — the console's drawer
        // and drag handler both carry `dur`, and `toCanonicalPayload` emits `duration_minutes`.
        // Omitting it here made the pre-flight answer `policy_failed` for the single most common
        // change the console actually submits.
        "duration_minutes",
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
