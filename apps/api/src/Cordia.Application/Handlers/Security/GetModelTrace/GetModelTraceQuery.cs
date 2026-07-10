using System.Text.Json.Serialization;
using Cordia.Domain.Security;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Security.GetModelTrace;

/// <summary>
/// Staff-only view of a stored model trace (spec 13 §25.3 stricter access tier, enforced by
/// the spec-14 spine). There is no customer-grant analog for telemetry — the ONLY path is a
/// staff role satisfying <c>operator</c> (owner ok, GD-1) with actor + reason code. Every
/// view, allow AND deny, appends the telemetry-plane audit spec 13 §4.3 explicitly permits
/// (<c>model_trace.viewed</c>).
/// </summary>
public sealed record GetModelTraceQuery(
    string TenantId,
    string TraceId,
    string? ActorId,
    string? ActorRole,
    string? ReasonCode) : IRequest<ModelTraceView?>;

/// <summary>The stored trace; null result ⇒ unknown/expired (404 at the controller).
/// snake_case wire shape for cross-service consistency.</summary>
public sealed record ModelTraceView(
    [property: JsonPropertyName("trace_id")] string TraceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("ts")] DateTimeOffset Ts);

/// <summary>Structural rules; authorization is the handler's spine.</summary>
public sealed class GetModelTraceValidator : AbstractValidator<GetModelTraceQuery>
{
    public GetModelTraceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Path-segment shape rejected as 400, not the store's fail-closed 500.
        RuleFor(x => x.TraceId).NotEmpty()
            .Must(ObjectStoreSegment.IsValid)
            .WithMessage("TraceId contains characters not allowed in a storage path segment.");
    }
}
