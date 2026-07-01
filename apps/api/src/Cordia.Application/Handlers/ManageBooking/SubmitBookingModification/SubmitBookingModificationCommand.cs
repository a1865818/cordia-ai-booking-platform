using System.Text.Json.Serialization;
using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.SubmitBookingModification;

public sealed record SubmitBookingModificationCommand(
    string TenantId,
    string RouteBookingId,
    SubmitModificationInput Request) : IRequest<SubmitBookingModificationResult>;

public sealed record SubmitBookingModificationResponse(
    [property: JsonPropertyName("status")] SubmitBookingModificationStatusEnum Status,
    [property: JsonPropertyName("operation_id")] string OperationId,
    [property: JsonPropertyName("intent_id")] string IntentId,
    [property: JsonPropertyName("intent_revision")] long IntentRevision,
    [property: JsonPropertyName("status_url")] string StatusUrl,
    [property: JsonPropertyName("accepted_at")] DateTimeOffset AcceptedAt);

public enum SubmitBookingModificationOutcome
{
    Accepted,
    Conflict,
    StaleVersion,
    ConfirmationInvalid,
}

public sealed record SubmitBookingModificationResult(
    SubmitBookingModificationOutcome Outcome,
    SubmitBookingModificationResponse Body);
