using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.BookingDrafts.ValidateBookingDraft;

/// <summary>
/// Backs the foreground tool <c>validate_booking_draft</c> (spec 03 §4.3, §6.1), which the
/// agent calls BEFORE it asks for availability.
///
/// The route it calls, <c>POST /api/booking-drafts/validate</c>, did not exist, and neither
/// did any other policy gate: "a table for 400 people at 7pm" and "a table at 3am" were both
/// carried straight into the availability lookup, which answers a capacity question and has
/// no opinion on whether the venue is open or the party is one the kitchen can seat.
/// </summary>
/// <param name="TenantId">Tenant scope resolved by <c>TenantGuard</c> (Inv-13).</param>
/// <param name="Draft">The agent-facing draft to check against tenant policy.</param>
public sealed record ValidateBookingDraftQuery(
    string TenantId,
    BookingDraft Draft) : IRequest<ValidateBookingDraftResult>;

/// <summary>
/// Per-tool status code plus the typed <see cref="PolicyValidationResult"/> payload the
/// controller flattens onto the wire (spec 03 §4.2/§4.3), the same shape
/// <c>check_candidate_availability</c> uses.
/// </summary>
public sealed record ValidateBookingDraftResult(
    ValidateBookingDraftStatusEnum Status,
    PolicyValidationResult Validation);
