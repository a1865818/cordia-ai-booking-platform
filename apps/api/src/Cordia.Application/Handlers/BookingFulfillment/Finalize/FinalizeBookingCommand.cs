using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Finalize;

/// <summary>
/// Finalizes a booking operation by calling the external booking provider
/// and transitioning to Booked (spec 06 §6.2, finalize step — canonical staged-worker example).
///
/// Implements the full §6.2 protocol:
///   TXN1 (claim + step + attempt planned) → circuit check → mark Dispatching →
///   external call → TXN2 (attempt result + step Succeeded + op Booked + outbox + audit).
/// </summary>
/// <param name="TenantId">Tenant that owns the operation.</param>
/// <param name="OperationId">Operation to finalize.</param>
public sealed record FinalizeBookingCommand(
    string TenantId,
    string OperationId) : IRequest<StepOutcome>;
