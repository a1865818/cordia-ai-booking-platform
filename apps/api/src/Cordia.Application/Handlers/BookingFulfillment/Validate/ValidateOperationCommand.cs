using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Validate;

/// <summary>
/// Validates an accepted booking operation and transitions it through
/// Accepted → Validating → ReservingCapacity (spec 06 §6.2, step "Validate").
/// </summary>
/// <param name="TenantId">Tenant that owns the operation.</param>
/// <param name="OperationId">Operation to validate.</param>
public sealed record ValidateOperationCommand(
    string TenantId,
    string OperationId) : IRequest<StepOutcome>;
