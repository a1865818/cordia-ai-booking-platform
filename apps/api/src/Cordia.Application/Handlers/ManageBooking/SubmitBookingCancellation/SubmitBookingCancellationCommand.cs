using System.Text.Json.Serialization;
using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.SubmitBookingCancellation;

public sealed record SubmitBookingCancellationCommand(
    string TenantId,
    string RouteBookingId,
    SubmitCancellationInput Request) : IRequest<SubmitBookingCancellationResult>;

public sealed record SubmitBookingCancellationResponse(
    [property: JsonPropertyName("status")] SubmitBookingCancellationStatusEnum Status,
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("intent_id")] string IntentId,
    [property: JsonPropertyName("intent_revision")] long IntentRevision,
    [property: JsonPropertyName("status_url")] string StatusUrl,
    [property: JsonPropertyName("accepted_at")] DateTimeOffset AcceptedAt);

public enum SubmitBookingCancellationOutcome
{
    Accepted,
    Conflict,
    StaleVersion,
    AlreadyCancelled,
}

public sealed record SubmitBookingCancellationResult(
    SubmitBookingCancellationOutcome Outcome,
    SubmitBookingCancellationResponse Body);
