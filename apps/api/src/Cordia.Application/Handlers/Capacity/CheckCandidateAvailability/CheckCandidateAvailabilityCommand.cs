using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.Capacity.CheckCandidateAvailability;

/// <summary>
/// Checks candidate availability for a booking draft and, when
/// <c>want_hold</c> is set, requests a bounded capacity hold (spec 03 §4.3,
/// spec 07 §4.3/§5).
/// </summary>
/// <param name="TenantId">Tenant scope resolved by <c>TenantGuard</c> (Inv-13).</param>
/// <param name="Request">The generated agent-facing availability request.</param>
/// <param name="HoldIdempotencyKey">
/// Hold idempotency key taken from the <c>X-Idempotency-Key</c> header, falling back to
/// <see cref="AvailabilityRequest.HoldIdempotencyKey"/>. Required when <c>want_hold</c> is
/// true; repeated calls with the same key return the SAME hold (Inv-5).
/// </param>
public sealed record CheckCandidateAvailabilityCommand(
    string TenantId,
    AvailabilityRequest Request,
    string? HoldIdempotencyKey) : IRequest<CheckCandidateAvailabilityResult>;

/// <summary>
/// Per-tool status code plus the typed <see cref="AvailabilityResult"/> payload
/// the controller flattens onto the wire (spec 03 §4.2/§4.3).
/// </summary>
public sealed record CheckCandidateAvailabilityResult(
    CheckCandidateAvailabilityStatusEnum Status,
    AvailabilityResult Availability);
