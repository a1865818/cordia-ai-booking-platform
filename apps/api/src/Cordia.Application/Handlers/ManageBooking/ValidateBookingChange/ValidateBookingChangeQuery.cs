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
    string GrantId) : IRequest<ValidateBookingChangeResult>;

public sealed record ValidateBookingChangeResult(
    string Status,
    BookingChangeValidation Validation);
