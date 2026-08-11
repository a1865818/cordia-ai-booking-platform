using System.Text.Json;
using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;

public sealed record ValidateBookingChangeQuery(
    string TenantId,
    string RouteBookingId,
    string TargetBookingId,
    long ExpectedBookingVersion,
    JsonElement RequestedFields,
    string GrantId,
    /// <summary>
    /// Asserted staff identity, the same pair <c>SubmitConfirmedIntent</c> accepts for a Modify
    /// (spec 08 §12 — a grant OR an authenticated staff role). Blank for the agent lane, which
    /// carries a grant instead.
    /// </summary>
    string ActorId = "",
    string ActorRole = "") : IRequest<ValidateBookingChangeResult>;

public sealed record ValidateBookingChangeResult(
    string Status,
    BookingChangeValidation Validation);
