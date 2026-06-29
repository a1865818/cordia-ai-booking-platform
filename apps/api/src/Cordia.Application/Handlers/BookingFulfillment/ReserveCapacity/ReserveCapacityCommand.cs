using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;

/// <summary>
/// Reserves capacity for a booking operation and transitions it from
/// ReservingCapacity → Held (spec 06 §6.2, step "ReserveCapacity").
///
/// Capacity/holds are owned by spec 07. For T4 this is a first-party no-op success.
/// </summary>
/// <param name="TenantId">Tenant that owns the operation.</param>
/// <param name="OperationId">Operation to reserve capacity for.</param>
public sealed record ReserveCapacityCommand(
    string TenantId,
    string OperationId) : IRequest<StepOutcome>;
