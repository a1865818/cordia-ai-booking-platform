using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.CancelBooking;

/// <summary>
/// Fulfils a <c>cancel</c> operation sitting in <see cref="Cordia.Domain.Enums.OperationStatus.Finalizing"/>
/// (spec 08 §5.3, spec 06 §11).
///
/// A cancellation is its <b>own</b> operation with its own id, idempotency key and canonical
/// hash — it does not mutate the original create operation, which stays <c>Booked</c> forever.
/// What changes is the booking row's lifecycle: <c>cancellation_pending → cancelled</c>.
/// </summary>
public sealed record CancelBookingCommand(string TenantId, string OperationId)
    : IRequest<StepOutcome>;
